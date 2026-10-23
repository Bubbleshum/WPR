# Working in this repo

## Reference projects

When researching how to shim a WP7 type or theme, the closest external prior
art is **yangzhongke/Windows-Phone-Emulator**
(https://github.com/yangzhongke/Windows-Phone-Emulator). It targets Silverlight
4 (not WPF/Avalonia) — its C# is a real prior implementation of WP toolkit
controls, but its Style / Setter / ControlTemplate parsing all defers to the
Silverlight XAML parser, so it's not transplantable for our XAML reader.

Worth lifting:
- `Microsoft.Phone/ThemeResources.xaml` + `Microsoft.Phone/System.Windows.xaml` —
  the WP7 typography + default control templates. Reference values for
  PhoneText*Style font sizes, FontFamily, brushes. Mirrored in our
  `PhoneTheme.cs`.
- `Microsoft.Phone.Controls/Panorama.cs` + `PanoramaItem.cs` + `Pivot.cs` —
  substantive (600+ LoC) reference for the swipe / parallax / layer logic.
- `Microsoft.Phone.Controls.Toolkit/Transitions/*.cs` — turnstile / swivel /
  slide transition state machines.

Not transplantable: the `Gestures/GestureHelper.cs` is a thin singleton
delegator with no inertia — we have to write our own pointer-events-to-gesture
pipeline on Avalonia.

Missing from that fork entirely: `LongListSelector`, `WrapPanel`,
`PhoneTextBox`, `PerformanceProgressBar`, `GestureService`/`GestureListener`,
plus `ButtonStyleLight`, `DarkThemePanoramaStyle`, and `PhoneApplicationPageStyle`
(app-supplied, not in the system theme).

## Running & build workflow

The user normally builds and runs from **Rider** (system .NET 10 MSBuild). The
CLI `dotnet build` path is for verifying small edits — it hits known
limitations on this machine and should not be the primary build mechanism.

### The whole repo is on .NET 10 (2026-09-25)

**There is no `net8.0` anywhere in the build any more.** Android moved first
(`net10.0-android36.0` for the head, `net10.0-android` for everything else); Windows
followed on 2026-09-25, so the TFM vocabulary is now exactly three strings:

| spelling | where |
| --- | --- |
| `net10.0` | shared projects, engine tier, modules, probes |
| `net10.0-windows10.0.17763.0` | the desktop head, D3D11 backend, WindowsToast, the harnesses |
| `net10.0-android` / `net10.0-android36.0` | android legs; the head pins the API level |

`global.json` pins the SDK to `10.0.100` with `rollForward: latestFeature`. **Older notes
in this file describing `net8.0` legs, `Microsoft.Android.Ref.34` and the 8.0 SDK band are
history** — they explain how the gating in `Src/Directory.Build.targets` came to be and the
traps inside it, all of which still apply, but the version numbers have moved on
(`WprAndroidApiLevel` is 36).

**Two security pins retired with the move, and they should NOT be re-added**:
`System.Text.Json` (in `WPR.Framework.Xna` and `WPR.Loader`) and `System.Formats.Asn1` (in
both heads). All three are part of the shared framework from net10, so an explicit 8.0.x
reference earns `NU1510` *and* names an assembly older than the runtime's own — the
opposite of what a security pin is for. Each site carries the full account of what it used
to cover. `System.Security.Cryptography.Xml` 8.0.4 is **not** in the shared framework and
stays. The one advisory left in the repo is `NU1904` on `System.Drawing.Common` 4.7.0,
reached transitively through `WPR.Notifications.WindowsToast`; it predates this move.

Verified on the day: desktop head 0 errors, Android head 0 errors (Release), all three
`scratchpad` probes ALL PASS, and Carcassonne, Galactic Reign, Cut the Rope and Sonic 4 all
run with the same draw counts they had on net8.

**A RID-specific publish of the Windows head MUST pass `-p:IncludeAndroidTargets=false`**, and
this is not the same thing as the auto-detection in `Src/Directory.Build.targets`. It bit the
release workflow immediately after the move:

```
error NU1102: Unable to find package Microsoft.NETCore.App.Runtime.Mono.win-x64
              with version (= 10.0.12)
  - Found 122 version(s) in nuget.org [ Nearest version: 9.0.0-preview.7.24405.7 ]
```

`--self-contained -r win-x64` propagates `RuntimeIdentifier=win-x64` into **every TFM of every
project reference**, the `net10.0-android` legs included. Those use the Mono runtime, so they ask
for `Microsoft.NETCore.App.Runtime.Mono.win-x64` — **a package Microsoft stopped publishing after
the 9.0 previews.** Under net8 it resolved and nobody noticed; under net10 there is nothing to
resolve and restore dies.

Three things make this hard to recognise:

- **A plain `dotnet build` never reproduces it.** No RID, no runtime pack, no failure — so the
  whole migration can look green and the release job still fail.
- **The failing set is the tell**: all 21 projects that multi-target `net10.0;net10.0-android` fail
  at once, while every single-TFM project (`WPR.Framework.Xna`, `FNA.Core`, the modules)
  restores fine. If a restore failure splits that way, this
  is it.
- **It reads as a workload problem and is not one.** The android workload being present or absent
  changes nothing; the RID is what does it.

`build-desktop.ps1` passes the flag for both its paths, and the release workflow passes it on the
publish, each with the reasoning inline. The Android job is unaffected — it builds
`-f net10.0-android36.0` with no `-r`.

**Avalonia is still split — 11.3.9 in the desktop head, 12.1.3 (`Avalonia.Android`, kept only for
its AppCompat theme resources) in the Android head — and that is NOT because the TFM blocked it.**
No shared project references Avalonia any more: `WPR.Framework.Silverlight` did, and carried this
split inside itself, until the Avalonia Silverlight host was deleted (Stage D, 2026-10-13). net10 removes the *restore* obstacle, and the desktop could take 12.1.3
tomorrow; what stops it is four migrations riding along behind the version number, chief among
them that `Avalonia.ReactiveUI` stops at 11.3.9 and its successor pulls **ReactiveUI 24**, where
`System.Reactive.Unit` became `RxVoid`. The full measured list is on the `AvaloniaVersion`
property in `WPR.Platform.Windows.csproj` — **read it before starting, because "bump the
version" is the wrong mental model.** A trial run on 2026-09-25 reached 84 compile errors with
more behind them, and Avalonia 12's XAML and theme changes do not surface at compile time at
all, so this needs a real pass through the UI afterwards.

### How runs actually happen

- **Build**: the user clicks build/run in Rider, which builds `WPR.Platform.Windows`
  for `net10.0-windows10.0.17763.0` and pulls in the rest by project reference.
- **Run**: `WPR.Platform.Windows` is the entry point (assembly/exe `WPR.Platform.Windows`, renamed from `WPR.UI.Desktop` on 2026-08-29). The UI lists installed games;
  picking one launches via `XnaLauncher.LaunchAsync` (every in-process title: XNA, Silverlight,
  WP8 native), or as a separate process through `UnityPortLauncher` (a `wpr-port.json`) or
  `GameMakerLauncher` (a GameMaker export with a Runner.exe available).
- **Run configurations are committed** (added 2026-08-30) and split across two
  mechanisms — don't add a third:
  - `Src/Platforms/WPR.Platform.Windows/Properties/launchSettings.json` — the three
    desktop profiles (plain run, `--repatch-installed`, `--reinstall-all`). Rider and VS
    surface these automatically and `dotnet run --launch-profile` uses them, so this is
    where a new *desktop* entry goes.
  - `Src/.run/*.run.xml` — Rider's shared run configurations, for the two things a launch
    profile can't express: the Android APK deploy, and mixed-mode native debugging
    (`MIXED_MODE_DEBUG`; the profile's `nativeDebugging` key is Visual Studio only).

  Rider reads `.run` from the **solution** directory, so it must stay at `Src/.run`, not
  the repo root — `$PROJECT_DIR$` in these files resolves to `<repo>/Src`. It can't live
  under `.idea/runConfigurations` because `.gitignore` excludes `.idea/` on purpose (it
  carries per-machine MSBuild paths), and git won't descend into an excluded directory to
  re-include a child. Two traps when editing these files: XML comments must not contain a
  double hyphen, and a config naming a project outside the opened `.slnf` shows as broken
  (the Android one is expected to be broken under `WPR.Windows.slnf`).
  A .NET run configuration **cannot** pass MSBuild properties, so `-p:IncludeAndroidTargets=false`
  is not expressible as one — use `build-desktop.ps1` or rely on the gating.

### Where the databases live (`Src/Core/WPR.Database`)

Centralised 2026-08-30. Everything persisted lives in one project:

- `Models/` — `WPR.Models.ApplicationContext` / `Application` / `ApplicationType`, the
  `applications.db` catalogue schema (moved out of `WPR.Loader`).
- `Migrations/` — its EF migrations. **Note these never run**: no `Migrate()` or
  `EnsureCreated()` exists anywhere in the repo. The schema comes entirely from the shipped
  `.db` files, so the 2022 migrations are inert artifacts kept for reference.
- `Data/` — the seed payload: `applications.db`, `achievements.db`, and the 277 per-game
  achievement catalogues under `Data/Achievements/<ProductId>/`.
- `Achievements/` — `AchievementContext` (the `achievements.db` schema), its migrations, and
  `EfAchievementStore`, which implements the seam below.

**`WPR.Framework.Xna` has no database dependency.** Stage 5e (2026-08-30) put the achievement
store behind `WPR.Xna.Achievements.IAchievementStore`, declared in `WPR.Framework.Xna` and
implemented here. Its built assembly now references **only `WPR.Common`** — no EF Core, Sqlite
or SQLitePCLRaw — which matters because that is the assembly patched games bind directly.

The seam lives in `WPR.Framework.Xna`, not `WPR.Abstractions`, for the same reason `WPR.Xna.Rhi`
does: its vocabulary is `Achievement`, a game-facing XNA type defined there. An interface in
Abstractions would force `Abstractions -> WPR.Framework.Xna` while the framework consumes the
seam — a cycle. (The unused DTO-shaped `IAchievementStore` stub that used to sit in
`WPR.Abstractions` was removed; a DTO contract would also have broken the entity tracking the
award path relies on.)

Registration is `XnaBackend.SetAchievements(...)`, called from **`ServicesSetup.Start()` in both
heads** — once at launcher startup, deliberately not per game. `XnaBackend.Clear()` runs on each
game teardown and does *not* clear this slot; clearing it would leave the second game launched
without achievements. If no store is registered, GamerServices degrades to "no achievements"
rather than throwing, matching the existing unseeded-product path.

### Platform input: the accelerometer is behind a seam, everything else already lives in a head

Motion input follows the same three-part shape as achievements (2026-08-30):

* **Contract** — `WPR.Engine.Sensors.IAccelerometerProvider`. It speaks
  `System.Numerics.Vector3` on purpose: the WP7 vocabulary (`AccelerometerReading`, the XNA
  `Vector3`) lives in the assemblies that *consume* this contract, so using it here would
  cycle. That is the same reasoning that put `IInputBackend` in `WPR.Xna.Rhi` — the difference
  is that a motion sample is three floats, so a neutral type costs one conversion instead of a
  whole vocabulary.
* **Registry** — `WPR.Engine.Sensors.SensorBackend`, its own project since the
  `WPR.Abstractions` dissolution. Its slot is `Accelerometer` / `SetAccelerometer`.
  It is **not** cleared at teardown (the provider is launcher-lifetime); what *is* cleared is
  the subscriber list, via `IAccelerometerProvider.ResetForNewLaunch()` from
  `ResetWprSingletons`. Skipping that reset reintroduces the 2026-08-08 ALC leak.
* **Implementations** — `Src/Modules/Input/` (see below). Both declared through
  `caps.Accelerometer(...)` in their head's `PlatformDescriptor`.

**The subsystem is sensors; the contract is one device** (renamed 2026-09-02 — it was
`ISensorProvider` with `SensorBackend.Provider`, and every one of its six members was an
accelerometer member, so only the name claimed otherwise). This is the
`AudioBackendRegistry.Sound/.Xact/.Media` shape: **one registry per subsystem, one narrowly
named slot per device.** A compass, gyroscope or motion source gets its own interface beside
`IAccelerometerProvider` and its own slot on `SensorBackend`, when something actually consumes
it — never more members on this one, and never a new project.

**The gyroscope is the second device (2026-10-09)**: `IGyroscopeProvider` (rad/s, WP7 device
frame, which is also Android's, so samples pass through unconverted), `SensorBackend.Gyroscope`,
`caps.Gyroscope(...)`, filled on Android by `WPR.Input.AndroidSensor.AndroidGyroscopeProvider`
(`[wpr-gyro]` in logcat). **Windows declares none**: a PC has no gyroscope. Its consumer is the WP8
native runtime, not a WP7 shim. `Src/Engine/WPR.Engine.Wp8Native/Runtime/Sensors.cs` implements WinRT's
`Windows.Devices.Sensors.Gyrometer` and `Accelerometer` (both were improvised stand-ins that
answered a placeholder object, so a game thought it had the sensor and read junk), backed by
`Wp8NativeSensorHost`. `GetDefault` answers nullptr when the platform has none, which WP8 games
were written to handle. Gyrometer speaks degrees per second, converted at that boundary.
`ReadingChanged` is raised from the frame loop, at most once a frame, with `recycle: true` on the
return trap. Modern Combat 4 does not subscribe: it polls `GetCurrentReading` about 3 times a
frame. Read `[wpr-wp8] sensors:` in the session log. No installed WP7 title uses
`Microsoft.Devices.Sensors.Gyroscope`, so that shim is still unwritten.

### Input implementations are modules (2026-09-02)

`Src/Modules/Input/` — no input implementation lives in a head any more, so adding one (a
controller, a gamepad-to-touch mapper) is a new project rather than an edit to a platform:

| module | TFM | fills |
| --- | --- | --- |
| `WPR.Input.Keyboard` | `net8.0` | `IAccelerometerProvider`, `IKeyboardEmulationHost` — tilt, Back key and synthetic touch |
| `WPR.Input.AndroidSensor` | `net8.0-android` | `IAccelerometerProvider` — the device's real sensor, straight off `SensorManager` |

**`WPR.Input.Keyboard` fills two seams on purpose.** Both describe the same 60 Hz emulator
from two directions — one is how the WP7 `Accelerometer` shim reads it, the other is how the FNA
backend's XNA components report keys into it. Splitting them would put one timer behind two
modules that then had to find each other.

**The Avalonia tie that used to pin this to the Windows head was an illusion.**
`KeyboardTiltBindings` had `ResolveAvaloniaKey(Avalonia.Input.Key)` and `ResolveXnaKey(Keys)` whose
bodies were character-identical — both `ToString()` the enum and compare against the persisted
name. One `ResolveKeyName(string)` replaces both, callers pass `key.ToString()`, and the UI
dependency disappears. Worth remembering as a pattern: **before accepting that something can't
leave a head, check whether the UI type is load-bearing or just a parameter type.** (The same move
freed the accent palette — there the answer was to keep the `IBrush` out of the shared data.)

What stays in the Windows head is `Input/TiltOverlay.cs` alone: it is an Avalonia `Control`, so it
genuinely cannot move.

**Splitting an Android module out of a head splits its NuGet graph, and that can break dex.**
`Xamarin.Essentials` 1.7.3 wanted the 2021-era AndroidX set including
`Xamarin.Google.Guava.ListenableFuture` **1.0.0.2**; the head unifies to **1.0.0.16** (via
`AndroidX.Core 1.12.0.2`). While Essentials was a direct head reference there was one graph and
NuGet unified it. As a module it restored independently, so the head got both — the old jar
embedded in the module's output and the new package from its own restore — and R8 failed with
`Type com.google.common.util.concurrent.ListenableFuture is defined multiple times`. The module
carried an explicit pin to 1.0.0.16 to collapse that back to one copy. **Do not reach for
`XamarinGoogleGuavaListenableFutureOptOut` instead** — see the long comment in
`WPR.Platform.Android.csproj`; those opt-outs SIGSEGV the launcher seconds after start. Expect this
class of failure whenever an android binding package moves out of the head, and expect it at dex
time rather than as a restore warning.

**Both the package and the pin are gone as of 2026-09-05** — `WPR.Input.AndroidSensor` now has no
package references at all — so the worked example above is history rather than live configuration.
It is kept because the failure mode is not: it is what the *next* android binding to leave the head
will do.

**Xamarin.Essentials was removed because its accelerometer is broken, not to tidy the graph.**
`Android.Hardware.SensorEvent.Values` compiles to
`JavaArray<float>.FromJniHandle(…, TransferLocalRef)` — every read mints a wrapper that **owns a JNI
reference and must be disposed**. Essentials' `OnSensorChanged` reads it three times (once per axis)
and disposes none, so at 50 Hz it leaks 150 JNI references a second; about a thousand samples in,
every reading collapses to a frozen `(-0.001, 0.000, 0.000)` and stays there for the life of the
process. Events keep arriving at full rate, the listener stays registered, `IsMonitoring` stays
true, nothing throws — the only symptom is that **tilt works for twenty seconds and then stops**
(reported against Doodle Jump, 2026-09-05). The rule this leaves behind: **anything reading a
`SensorEvent` calls `Values` ONCE per event, copies the floats out, and disposes it.** Indexing it
per-axis is not merely wasteful, it is the bug.

Two traps when diagnosing a repeat of this. The reader count and the start/stop lines cannot show
it, because none of them change — which is why `[wpr-accel]` reports the *edges* of an implausible
magnitude (`READINGS WENT FLAT` / `readings recovered`) and carries `|a|`; a real accelerometer
always measures gravity, so a sustained `|a| ≈ 0` means the sensor has stopped reporting physics
while still delivering events. And the per-game `wpr_game_debug.log` is **per Android user**: on a
device with a Samsung `DUAL_APP` clone (user 95) or Secure Folder (150), `run-as` reads user 0's
copy and will happily serve a stale log from a different run. Read logcat instead, or check
`pm list users` first.

`Start`/`Stop` are **counted, not idempotent**. One provider is shared by every
`Accelerometer` a game holds, so it refcounts its readers and powers the sensor down on the
last stop — on a phone that is battery, and it is why WP7 titles stop their sensor per screen.
Two consequences for anyone adding a provider: subscribe to your source *idempotently* (a
second start must not double-deliver, which a per-instance subscription used to prevent for
free), and make `ResetForNewLaunch` stop unconditionally rather than honouring the count — a
game that exited without stopping is the case it exists for. `Accelerometer` holds the exact
provider instance it started against so the pairing can't drift.

**Exactly one already-in-flight sample can land after a stop or reset**, and that is inherent,
not a bug to chase. `KeyboardAccelerometerHost.OnTick` raises its event *outside* its own lock
on purpose — invoking a game handler under that lock would let a slow handler block the 60Hz
timer or deadlock against it — so a tick already past the lock cannot be recalled. Both
providers short-circuit on `_consumers == 0`, which makes the post-`Stop()` case exact and
narrows the teardown one to a single sample. It cannot pin an ALC either way: the subscriber
list is already empty, so no new reference forms.

**Diagnosing tilt.** Both providers write `[wpr-accel]` lines to the per-game
`wpr_game_debug.log` (via `Trace`, which `ApplicationLaunch` routes there): one per
start/stop with the reader count, and a sampled `tick #N reading=(…) orient=… readers=N`
roughly every two seconds. That trace is the first thing to look at when tilt is reported
unresponsive — it says whether readings flow at all, and which orientation the key intent is
being rotated into. The desktop half of it predates the split; Android had no equivalent
until the providers separated.

`Microsoft.Devices.Sensors.Accelerometer` is now platform-free; before this it carried both
implementations behind `#if __MOBILE__ || __ANDROID__`, which shipped the desktop emulator
inside the APK and put an Android sensor package on a shared framework project.

**Everything else input-shaped is already in the right head** and needs no seam — the WP7 bezel
buttons (`PhoneHardwareButtons`, Avalonia) and the keyboard→tilt bindings on Windows, the Back
key routing in `GameActivity.OnBackPressed` on Android. The XNA device API (keyboard, mouse,
gamepad, touch) is a separate, already-solved seam: `WPR.Xna.Rhi.IInputBackend`, implemented by
`WPR.Backend.FNA` over SDL for both platforms.

### One keyboard, three emulated devices (2026-09-03)

The desktop keyboard now stands in for the accelerometer, the hardware Back button **and** the
touchscreen, all through one module (`WPR.Input.Keyboard`) and one seam.

**The seam is `WPR.Xna.Rhi.IKeyboardEmulationHost`** — renamed from `ITiltEmulationHost` when it
stopped being about tilt. `XnaBackend.KeyboardEmulation` / `SetKeyboardEmulation`, declared through
`caps.KeyboardEmulation(...)`. Android registers none, so every path below degrades to "absent".

| emulated device | binding lives in | reaches the game as |
| --- | --- | --- |
| accelerometer | `Configuration.TiltKey*` (global) | `Accelerometer` readings via `IAccelerometerProvider` |
| hardware Back | `Configuration.BackKey`, default `"Escape"` (global) | one frame of `GamePad.Buttons.Back` |
| touch tap/swipe | `input-bindings.json` in the game's install folder (**per game**) | `TouchPanel.GetState()` **and** `ReadGesture()` |

**Key events, not polled key state — this was measured, not reasoned.** Back and touch gestures are
both triggered from `SDL2_FNAPlatform.PollEvents` on a non-repeat keydown, asking
`IsBackKey(key)` / `NotifyKeyDown(key)`. The obvious alternative is to test
`ReportPressedKeys` in the XNA input component, and it is wrong: `Keyboard.GetState()` is a
per-frame snapshot, so a tap that goes down and up inside one 16 ms frame never appears in it. A
synthetic `{ESC}` was dropped **every** time; a human press spans several frames and usually
survives, which is what makes the polled shape a bad bet — it fails rarely and unreproducibly.
`SDLK_AC_BACK` stays hardcoded beside the binding query because it is the hardware button, not a
preference, and it is Android's only path.

**On Android, Back is the hardware Back button and nothing else — this is a product decision, not
an accident of the implementation.** No external controls emulate it. It holds today because
`AndroidPlatform` declares no `KeyboardEmulation`, so `XnaBackend.KeyboardEmulation` is null and
the binding half of the test above short-circuits; what remains is `SDLK_AC_BACK` (delivered
because `SDL_HINT_ANDROID_TRAP_BACK_BUTTON` traps it) and `GameActivity.OnBackPressed` →
`IGameHost.PressBackButton()`, both unconditional. **Do not declare a keyboard-emulation host on
Android** — a physical keyboard on a tablet or Chromebook would look like a reason to, and it would
silently make Back rebindable there.

Note this *narrowed* Android behaviour on 2026-09-03: `SDLK_ESCAPE` used to be hardcoded here
too, so Escape on a connected Bluetooth keyboard would assert Back on a phone. It no longer does,
which is the intended behaviour.

**The touch injector is a decorator over `IInputBackend`, NOT a `GameComponent`.** This is the
trap, and the tilt precedent actively misleads here. `TouchPanel.Update()` runs
`GestureDetector.OnUpdate()`, snapshots previous touches, and only *then* calls
`UpdateTouchPanelState()` — which writes real fingers and clears every slot it owns. A component
runs earlier in the tick, so its writes are erased in the same frame: state never appears in
`GetState()` while gestures still work, which reads as "the touch plumbing is broken".
`SyntheticTouchInputBackend` wraps `FnaInputBackend` in `FnaGameHost`, making it the last writer by
construction.

Three more things it has to get right, each of which silently half-works if missed:

- **Both channels.** `SetFinger` fills `GetState()`; `INTERNAL_onTouchEvent` feeds `GestureDetector`
  (i.e. `ReadGesture()`). They are unconnected and WP7 titles use both.
- **Different coordinate spaces.** `SetFinger` takes **display** coords; `INTERNAL_onTouchEvent`
  takes **normalised** ones and scales them itself. Passing display coords to the latter puts every
  gesture off-screen by a factor of the display size.
- **A reserved slot and a stable finger id.** `TouchPanel.ReservedFingerSlots` (default 0) excludes
  the top slots from the drain's writes *and* its clear loop — without it there is no free slot,
  since the drain accounts for all 8. The synthetic finger is `int.MaxValue - 1` (the mouse is
  `int.MaxValue`) and must not change mid-drag: `GestureDetector` tracks one active finger id and
  abandons the gesture if it does. Verified in the log as `slot=6 finger=2147483646`.

**`JsonStringEnumConverter` is required on the bindings reader.** Without it `System.Text.Json`
will not map `"Tap"`/`"Swipe"` onto `KeyboardTouchGestureKind` and throws on the whole document, so
every binding is lost. It cost a full test cycle because the failure was reported through
`WPR.Common.Log`, which writes to **stdout — discarded by a `WinExe`**. Binding diagnostics now go
through `Trace` (which reaches the per-game `wpr_game_debug.log`) and log the count
**unconditionally, including zero**: "no bindings loaded" and "the feature never ran" are otherwise
indistinguishable from inside a game. `scratchpad/bindcheck` round-trips a real file through the
real loader without launching anything — do that before testing a parser change in the app.

**Reading a working run**, in order; each line rules out a different failure:

```
[wpr-input] 2 touch binding(s) loaded from …\input-bindings.json
[wpr-input] gesture start T: tap (400,280)
[wpr-input] synthetic touch Pressed  at (400,280) slot=6
[wpr-input] synthetic touch Released at (400,280) slot=6
```

**The editor is per game, from the game pane** ("Controls", beside Info/Uninstall) — a
`PhoneGesturePad` you draw the gesture on, a to-scale phone outline rather than a screenshot, so no
frame grab is needed and it works before the game has ever run. Per game because the data is: the
coordinates only mean anything against one title's layout, and the file dies with the install.
Bindings are read in `PrepareForLaunch`, so an edit applies from the **next** launch.

**Silverlight titles get it too since Stage C (2026-10-13)**: they now run on the XNA host and read
touch through `TouchPanel` → `SilverlightTouchRouter`, so these bindings apply.

### Vibration is its own subsystem, and it is NOT the XNA rumble API (2026-09-05)

`Microsoft.Devices.VibrateController.Start`/`.Stop` were **empty method bodies** from the day the
shim was written, so every WP7 title that buzzed on a collision, a wrong answer or a menu tap did
nothing at all — silently, on both heads. They now go through a seam with the same three-part shape
as sensors and achievements:

* **Contract** — `WPR.Engine.Vibration.IVibrationProvider`: `IsSupported`,
  `Vibrate(TimeSpan, float intensity)`, `Stop()`. It speaks `TimeSpan` and a float on purpose —
  `VibrateController` lives in `Microsoft.Phone`, which *consumes* this, so naming it here would
  invert the reference. Same rule as everywhere in the engine tier.
* **Registry** — `WPR.Engine.Vibration.VibrationBackend`, slot `Device` / `SetDevice`. Not cleared
  at teardown (the provider is launcher-lifetime); it is **stopped**, from
  `ResetWprSingletons` — a game that exits mid-buzz never calls `VibrateController.Stop()` itself.
  There is no `ResetForNewLaunch` counterpart to the sensor one because this seam is **push-only**:
  no event, so no subscriber list and no ALC to pin.
* **Implementation** — `Src/Modules/Vibration/WPR.Vibration.AndroidVibrator`, declared through
  `caps.Vibration(...)`. **Windows declares none** and needs none: a desktop PC has no motor, so
  the desktop behaviour is unchanged and absent means "this platform does not have it".

**`intensity` exists for a device that does not exist yet, and that is deliberate.** WP7's API has
no amplitude concept — `VibrateController` passes `1f` — but controller rumble is fundamentally
amplitude-based, and a contract without it would have to be widened the day the second
implementation landed. **Where controller rumble goes: a `Controller` slot beside `Device`, filled
by a `WPR.Vibration.Gamepad` module implementing the same interface** — not a second project, not
more members here, and *not* a second `IPlatformCapabilities` member (a pad is present or absent at
runtime on both heads, so it is not a fact about the platform). `VibrateController` then picks
between the slots; that policy is worth deciding with a real pad in hand.

**Do not route `GamePad.SetVibration` through this.** XNA gamepad rumble already exists and already
works — `WPR.Xna.Rhi.IInputBackend.SetGamePadVibration` → SDL, on both heads. It is per-pad,
per-motor and *level-based* (runs until changed, no duration); this seam is one-shot with a
duration. Different APIs, different lifetimes, and a game calling one is not asking for the other.
What the new seam adds is letting a title that only knows the WP7 handset API reach whatever the
player is actually holding.

Three things about the Android implementation:

- **Three API generations.** `VibrationEffect.CreateOneShot` from 26 (the only place amplitude
  exists at all, and only when `HasAmplitudeControl` — asking a motor without it for a specific
  amplitude is rejected, not rounded, hence `DefaultAmplitude`); the deprecated `Vibrate(long)`
  below that; and `VibratorManager.DefaultVibrator` from 31, where `VIBRATOR_SERVICE` is deprecated.
  Guards are `OperatingSystem.IsAndroidVersionAtLeast(n)`, **not** the
  `Build.VERSION.SdkInt >= BuildVersionCodes.X` form used in `WPR.Notifications.AndroidChannel` —
  equivalent at runtime, but only the former is understood by the platform-compat analyzer, so the
  module carries no CA1416 suppressions. Deliberate divergence, noted in both files.
- **`android.permission.VIBRATE` is required**, and it is a *normal* permission — granted at
  install, nothing for `MainActivity` to request (unlike `POST_NOTIFICATIONS`). Without it every
  call throws `SecurityException`, which the provider logs as `[wpr-vibrate]` and swallows, so the
  symptom is games that silently never buzz rather than a crash. Deliberately **no**
  `<uses-feature android:name="android.hardware.VIBRATE">`: it would let Play filter WPR off
  tablets with no motor, and `Vibrator.HasVibrator` already gates it at runtime.
- **`GameActivity.OnPause` stops it**, same reasoning as the song: the buzz is ours and nothing else
  cancels it, so a game backgrounded mid-vibration keeps the phone shaking on the home screen.
  There is deliberately **no `OnResume` counterpart** — a vibration is a one-shot event, not a
  stream with a position.

**Beware `Trace` in an Android module**: `Android.OS.Trace` collides with `System.Diagnostics.Trace`
and CS0104s a bare `Trace`. This module aliases (`using Trace = System.Diagnostics.Trace;`); the
accelerometer module never hit it only because it does not `using Android.OS`.

**Read the platform line** — `vibration=` joined the `[wpr-platform]` summary, so one line still
says how the device was set up:

```
[wpr-platform] Android: accelerometer=EssentialsAccelerometerProvider vibration=AndroidVibratorProvider driver=Vulkan …
```

`vibration=none` means composition, not hardware. `[wpr-vibrate] vibrator resolved — hasVibrator=…`
in the per-game log is the hardware answer, and the two are otherwise indistinguishable from inside
a game. **Emulators report `hasVibrator=false`** and exercise only the degradation path, so judge
actual buzzing on real hardware.

**The global on/off switch is `VibrationBackend.IsEnabled`, and it gates BOTH vibration paths.**
Persisted as `Configuration.VibrationEnabled` (nullable, defaults true, so a config.json written
before the setting existed reads as "on" — the `TiltSimulationEnabled` precedent), and surfaced on
the **Android settings page** as a WP-styled switch with an on/off word beside it.

The non-obvious part is the second consumer. `GamePad.SetVibration` / `SetTriggerVibrationEXT` in
`WPR.Framework.Xna` honour the same flag, **even though gamepad rumble never touches this
registry** — it goes to SDL through `IInputBackend`. Only the *preference* is shared, and it has to
be: a switch labelled "vibration" that left a connected pad shaking is a bug. That is the one
reason `WPR.Framework.Xna` references `WPR.Engine.Vibration`, and the one reason
`WPR.Engine.Vibration` references `WPR.Common`. Three rules fall out:

- **The motors are zeroed, the call is not skipped.** `SetVibration` returns "false when the pad has
  no rumble motors"; short-circuiting would conflate a *muted* pad with a *motorless* one, and
  would also strand a rumble a game started before the switch was read.
- **Only calls that START a vibration consult it.** `IVibrationProvider.Stop` must run
  unconditionally — teardown and `OnPause` silence the motor regardless, and a stop that honoured
  the preference could strand a buzz that began while it was on.
- **A future `Controller` slot needs no extra work**: anything reading `IsEnabled` is already
  covered.

**The setting is read live, and takes effect from the next game launch.** That is not a compromise:
`GameActivity.OnDestroy` calls `Process.KillProcess(MyPid())`, so every launch gets a fresh
`:game` process that re-reads config.json. Reading live rather than capturing at composition keeps
it correct if that kill is ever removed.

**There is no desktop UI for it yet** — a PC has no motor, so the only thing it would mute there is
pad rumble. The setting itself is cross-platform, so adding a Windows checkbox is UI work only.

`WpTheme.ApplySwitch` is the accent tint for a `Switch`, and it is the first toggle control in the
Android shell. Note `ThumbTintList`/`TrackTintList` are **API 23+** while this app's minimum is 21,
so it returns early below 23 and the switch keeps the platform's own colours.

No `ApplicationPatcher.Version` bump and no reinstall — no patcher table changed and no IL is
rewritten. Games pick this up on next launch.

### Songs stream on FAudio again, on both heads; WP7 movies play (2026-10-12)

**Songs.** `WPR.Audio.FAudio.StreamingSongPlayer` replaced FAudio's native `XNA_Song`, whose single
one-second buffer (queue depth one, refilled from `OnBufferEnd`) clicked once a second on phones and
made `XNA_GetSongEnded` answer "ended" in the gap between buffers. A decode thread (NVorbis, MIT,
managed) keeps six 125 ms buffers queued on an FAudio voice, found by polling `GetState`, so no
native-to-managed callback is involved. Android no longer registers `AndroidMediaPlayerModule` (kept
as an unregistered fallback). Measured on Pixel_Dev: 0 AudioFlinger underruns on both tracks over a
minute of Mirror's Edge; desktop 0 underruns.

Three things are load-bearing:

- **Songs keep their own FAudio engine.** Sharing the sound-effect engine would put music under its
  mastering voice, and that voice's volume is `SoundEffect.MasterVolume`.
- **So the platform does not pause it.** SDL's OpenSL ES driver pauses only one output device; the
  song track stayed Active on the home screen. `AudioBackendRegistry.SuspendForBackground` /
  `RestoreFromForeground` (called from `GameActivity.OnPause`/`OnResume`) reach any backend that
  implements `IBackgroundAwareAudio`. A song started while suspended is created but not started.
- **The Android 1024-frame quantum** (`FAUDIO_1024_QUANTUM`) is on the song engine too; `XNA_SongInit`
  never asked for it.

Log: `[wpr-song] playing <file>: <rate> Hz xN, <len> s` and, at stop, `underruns=N`.

**Video.** 87 `.wmv` and 41 `.mp4` across ~55 library titles used to be skipped as if finished.
Seam: `WPR.Engine.Audio.IVideoDecoder` / `VideoDecoderBackend`, declared by `caps.VideoDecoder(...)`.
`WPR.Xna.Media.WprMoviePlayer` is the shared player (wall clock, newest due RGBA frame, sound through
a `DynamicSoundEffectInstance`).

| head | decoder | how |
| --- | --- | --- |
| Windows | `FFmpegVideoDecoder` | two ffmpeg.exe pipes per movie (RGBA frames, s16le), straight from the original WMV/MP4 |
| Android | `WPR.Video.MediaCodec` | MediaExtractor + MediaCodec (YUV_420_888 to RGBA) over `<movie>.wpr.mp4`, NVorbis over `<movie>.wpr.ogg` |

Android needs sidecars because MediaCodec has no WMV. They are written at install and at **repatch**
(`VideoCompabilityConverter`, never fatal) by the `:transcode` process
(`FFmpegKitAudioTranscoder.TranscodeVideoAsync`: MP4 video stream copied, anything else to MPEG-4
Part 2, the only video encoder this ffmpeg-kit build has; sound to Vorbis). **Games installed before
this need a re-patch** to get their cutscenes on Android. An MP4 without sidecars is tried as is.

- **`MediaPlayerLauncher`**: `LauncherMovieOverlay`, an `IHostOverlay` that `Game.Tick` runs in place
  of the game's Update/Draw (WP7's system player took the screen the same way). Landscape movies are
  turned a quarter on a portrait backbuffer, Back always leaves, a tap skips only when `Controls`
  allows Skip or Stop. Completion runs the existing reactivation. The game's MediaPlayer song is
  paused for the movie. Teardown clears the overlay with `completed: false`, before audio teardown.
- **`MediaElement`**: painted by `SoftwareVisualRasteriser` (its signature hashes `FrameVersion`).
  **`Play()` raises nothing synchronously**: Sid Meier's Pirates! sets the flag its
  `CurrentStateChanged` handler needs only after calling `Play()`, so a synchronous `Opening` left its
  black cover over the whole film. The element **measures from the video's natural size and re-lays
  itself out** in its last slot when that size arrives, because the mixed-mode host runs layout only
  on navigation (Pirates puts the movie at `VerticalAlignment="Top"`, height 0 otherwise).
- Without a decoder, or with an unplayable file, both fall back to the old instant "ended".

Verified: desktop NFS Undercover (`MediaPlayerLauncher`, via the harness's `movie` verb) and Pirates!
(`MediaElement`, 640x480, subtitles and Skip button over it); Android Pixel_Dev, Cut the Rope's intro
through MediaCodec after a UI re-patch wrote its sidecars, played to its end. Not run: S24, Mali.
Two things seen on the way and not caused by this: Pirates! NREs in its own game thread
(`ISEUpdateCommandBuffer.Update`) right after the opening movie on the old build too; and the
harness's synthetic tap reports `Released` for more than one frame, so Cut the Rope's "any touch skips"
skips its intro in the harness (a real tap on Android does not).

No `ApplicationPatcher.Version` bump: Android sidecars come from a re-patch, which runs regardless.

### WP8 sound goes through the registry; streaming voices on AudioTrack (2026-10-13)

`WPR.Engine.Wp8Native.AudioTrackOutput` (WP8 voices straight onto Android `AudioTrack`s, with
`SincResampler`) sat outside `AudioBackendRegistry`, so it never showed in the `[wpr-audio]` line
and only WP8 titles could use it. It is now a module, **`Src/Modules/Audio/WPR.Audio.AudioTrack`**
(item 3b of the runtime convergence plan), and `Wp8NativeAudio` (one `DynamicSoundEffectInstance`
per XAudio2 voice) is the only `IHostAudio` on both heads.

**What the module covers: the streaming-voice half of `IAudioBackend`, nothing else.**
`AudioTrackSoundBackend` decorates the FAudio sound backend. A voice is claimed when it is created
through `CreateSourceVoice` with 16-bit PCM, which in the framework means a
`DynamicSoundEffectInstance` (static effects use `CreateSourceVoiceRaw`; the movie player's float
voices stay on FAudio too). Every call on an unclaimed handle, and the device, 3D maths and the
microphone, goes to `next`. Handles are one byte of `AllocHGlobal` each, so they can never equal a
live FAudio voice pointer. The launch line then reads `sound=AudioTrack`, i.e. the top of the chain.

Things it has to do because the tracks are not under FAudio's mastering voice or SDL's device:

- **`SoundEffect.MasterVolume`** is multiplied into each track's volume (`SetMasterVolume` is
  intercepted, then forwarded).
- **Background**: the backend is `IBackgroundAwareAudio`, reached from `GameActivity.OnPause`, so
  `Wp8NativeAudio.Suspend` is a no-op on both heads now.
- **The output matrix** (XNA pan, `Apply3D`) is applied in software: every track is **stereo**, a
  mono source is duplicated through `[L, R]` gains (FAudio's default `[1, 1]`). Reverb sends and
  filters are not supported on these voices and are logged once each.
- **Tracks are pooled** (2 idle per rate/channel pair, 8 in all). XAudio2's `FlushSourceBuffers`
  becomes `DynamicSoundEffectInstance.Stop()` + `Play()`, which destroys and re-creates the voice; a
  new `AudioTrack` and writer thread for every flush would be wasteful. A pooled track is paused,
  flushed and has its resampler reset; its underrun counter is re-baselined on reuse.

Everything the old class learned is kept and is in `AudioTrackVoice`: native output rate plus our
own sinc resampler (AudioFlinger's resampler made MC4's 32 kHz mix dull), the 10 ms start
threshold (API 31+; silence priming below it), non-blocking writes from one writer thread per
track, and "buffer finished" read off the playback head.

**Scope: WP8 titles only, by default (measured on the S24, 2026-10-14: WP8 underruns equal to the old class; FAudio's AAudio MMAP output reports none to AudioFlinger, so XNA cannot be compared and stays on it).** The module
is registered as `AudioTrackModule(AudioTrackScope.LowLatencyRequests)`: it claims voices only while
`AudioBackendRegistry.LowLatencyStreaming` is set, which `Wp8NativeAudio`'s constructor does and
every `Compose()` clears. XNA titles' dynamic instances therefore stay on FAudio exactly as before.
The plan's risk note says to switch them only if a phone measurement shows FAudio underrunning for
them. The installed XNA titles that use `DynamicSoundEffectInstance` (byte scan of the desktop
installs): **Pitfall!** (`55ebed63…`, Krome's Atari emulator streams its audio), **Feed Me Oil**
(`5de64ff7…`, `OggSound`), **Little Acorns** (`7a5dcbbb…`, `TwoDeeXNA`) and **FC Rocket**
(`e77de387…`, `FCArcadeLib`); the movie player is the fifth user but submits float.

**A/B on a device without a rebuild:** `audiotrack_scope.txt` beside `fna3d_driver.txt` in the
app's external files directory. `all` puts every 16-bit dynamic instance on AudioTrack, `off`
registers no module (WP8 back on FAudio, as on the desktop), anything else or no file is the
default. Read in `AndroidPlatform.ChosenAudioTrackScope`; the `:game` process re-reads it every
launch. Measure with `adb shell dumpsys media.audio_flinger` (per-track underrun frames for the
`:game` pid) and the `[wpr-audiotrack]` lines (`voice … played at 48000 Hz stereo`, and at device
close `underruns=N`) in `Logs/wpr-game.log`.

Desktop is unchanged: no module is registered there, so the flag is read by nothing and WP8 voices
stay on FAudio. Verified with Modern Combat 4 in `scratchpad/gamerun`: `[wpr-audio] sound=FAudio`,
one source voice, buffers and callbacks advancing through menus and play. Its `underruns=` counter
(frames on which a playing voice had nothing queued) climbs mainly during loading, when the guest
runs at 1-2 fps; it moved 403 → 408 over ~300 frames of steady play.

No `ApplicationPatcher.Version` bump and no reinstall.

### FAudio has its own AAudio output on Android (2026-10-13)

On Android 8.1+ every FAudio engine (sound effects/XACT, songs, WP8 voices when routed there) opens
its own low-latency **AAudio** stream instead of SDL's audio device: `FAudio_platform_aaudio.inl`,
included by `FAudio_platform_sdl2.c` under `__ANDROID__`, rebuilt into all three `libFAudio.so`
(same NDK recipe as FNA3D, sources `lib/FAudio`; exported symbols unchanged). The callback mixes one
FAudio quantum at a time into a FIFO; the stream buffer starts at two bursts and grows a burst on
each xrun. Log: `FAudio: AAudio output: ...` (tag `SDL/APP`).

Why: SDL2's Android drivers open a plain track whose large buffer they keep full, so FAudio mixed
~130 ms ahead of the speaker (Android reported ~175 ms latency on the emulator, now ~125). And
**SDL allows only ONE output device on Android** (`OpenAudioDevice failed: Audio device already
open`), so the songs engine, which opens second, got no sound at all on the SDL path. Below 8.1, or
if AAudio will not open, FAudio falls back to SDL by itself, with that limit. There is no switch for it.
Note the env file cannot set SDL hints any more: under CoreCLR,
`AndroidEnvironment` values reach managed code but not SDL's `getenv`.

SDL no longer pauses this output, so an SDL event watch pauses every stream on
`SDL_APP_DIDENTERBACKGROUND` and restarts it on `WILLENTERFOREGROUND` (verified: tracks `A` → `P` →
`A`). A disconnected stream is reopened on its own thread.

**WP8 voices stay on `AudioTrackOutput` on Android.** Angry Birds Rio on the emulator, 50 s:
22-43 dry spells through AudioTrack, 56-135 through FAudio over AAudio, ~170 through FAudio over
SDL. The emulator has no fast mixer, so the S24 may differ; trying it means changing
`IHostAudio.Create`. The emulator degrades after ~6 game runs (fps falls,
everything underruns); cold-boot it between measurement sets. Not run on hardware yet.

**Read with the AudioTrack section above (merged 2026-10-14):** "WP8 voices stay on
`AudioTrackOutput`" below is now true through the `WPR.Audio.AudioTrack` module, which replaced
that class. Trying WP8 on FAudio-over-AAudio is `audiotrack_scope.txt` = `off`, no code change.

### WP7 launchers leave the game through `IUriLauncher` (2026-09-24)

`Microsoft.Phone.Tasks.WebBrowserTask.Show()` was an empty method, so every link a WP7 title opened
did nothing. **Cut the Rope is the reference case**: its Cartoons are not in-game video at all —
each episode is a `WebBrowserTask` to `vnd.youtube:<id>` (Youku on Chinese locales), and on a first
run *Play* opens episode 1 before the level select. The tap did nothing while the game still marked
the episode watched.

Same three-part shape as vibration:

* **Contract** — `WPR.Engine.Launchers.IUriLauncher` (`bool TryOpen(Uri)`, must not throw). Speaks
  `System.Uri` only; "Launchers" is WP7's own word for the tasks that leave the app.
* **Registry** — `WPR.Engine.Launchers.LauncherBackend.Uri`, filled through `caps.UriLauncher(...)`.
  Process-lifetime, push-only, nothing to reset. Null means every launcher stays a no-op.
* **Implementations** — `Src/Modules/Launchers/`: `WPR.Launchers.ShellExecute` (Windows,
  `Process.Start` with `UseShellExecute`) and `WPR.Launchers.AndroidIntent` (`ACTION_VIEW` +
  `NEW_TASK` on the application context, so a youtube.com link lands in the YouTube app). No
  manifest change: `ACTION_VIEW` needs no permission, and `resolveActivity` is deliberately not
  called, which keeps Android 11 package visibility out of it.

**Policy lives in the shim, not the launcher**, because only the WP7 side knows it
(`WebBrowserTask.Normalise`, unit-tested in `WPR.SilverlightCompability.Tests`):
`vnd.youtube:<id>` becomes `https://www.youtube.com/watch?v=<id>` (nothing on a desktop claims the
WP7 scheme, and Android still routes the https form to the YouTube app); a bare `www.` host gets
`http://`; and **only http and https are ever opened**. The URI is game-supplied data, and on
Android any other scheme is an intent any installed app may claim.

Read it out of the log: `launcher=` and `share=` joined the `[wpr-platform]` line, and every attempt writes
`[wpr-launch] opened …` / `refused …` / `no launcher on this platform` through `Trace`. Verified on
the S24: Cut the Rope → YouTube app on the episode → back to the game's "Episode 1: Replay / Share"
screen. The first frame after returning fails to acquire a swapchain image, and that is normal
resume behaviour.

**Sharing is a second slot, not a second member**: `IShareSheet` (`TryShare(subject, text)`),
`LauncherBackend.Share`, `caps.ShareSheet(...)`. Opening a link and sharing one are different OS
facilities, and the desktop has the first but not the second. `ShareTaskBase.Show()` asks the
concrete task to `Describe` itself: `ShareLinkTask` shares `Message + " " + link`, with `Title` as
the subject, and its link goes through the same `WebBrowserTask.Normalise` rewrite so a
`vnd.youtube:` link is shared as youtube.com. A link that `Normalise` refuses is still shared,
verbatim, because here it is only text the player chooses to send. `ShareStatusTask` shares
`Status`. Android is `WPR.Launchers.AndroidIntent.AndroidShareSheet`, an `ACTION_SEND text/plain`
wrapped in `createChooser`; `NEW_TASK` has to go on the **chooser** intent, since that is the one
started from a non-activity context. **Windows declares none**: its share UI is WinRT
`DataTransferManager`, bound to a window handle, so the task logs `no share sheet on this platform`.
Verified on the S24: Cut the Rope's Share button opens the system chooser showing "Cut the Rope
Cartoons: Episode 1 https://www.youtube.com/watch?v=bj3cbCE56wQ"; Back returns to the game.

**Next callers, not yet wired:** `EmailComposeTask` (`mailto:` would need adding to the allowed
schemes) and `BingMapsTask`. `MarketplaceDetailTask` stays empty on purpose (see its remarks).

No `ApplicationPatcher.Version` bump and no reinstall — shim behaviour, picked up on next launch.

### Where the WP8 native runtime lives (moved 2026-10-13)

It used to be `Research/Wp8Native/`, compiled into the engine by a wildcard. Now:

| what | where |
| --- | --- |
| the runtime (PE loader, CRT, WinRT, D3D, media...) | `Src/Engine/WPR.Engine.Wp8Native/Runtime/`, namespace `WPR.Wp8Native` |
| the wprcpu C++ shim and the committed `wprcpu.dll` | `Src/Engine/WPR.Engine.Wp8Native/Native/` |
| the Unicorn probe, `run.ps1`, the WinForms host | `Tools/Wp8/Wp8Probe/` |
| contract tests, census, module probe, Wp8Run, WinRtSlotGen | `Tools/Wp8/` |
| the probe's research log and screenshots | `Docs/Wp8Native/` |

**Nothing in `Runtime/` may name an XNA or engine type**: the probe compiles those same files with
no XNA reference (and with the GPL Unicorn CPU, which is why it never ships). XNA-facing code goes
in the project root beside `Wp8NativeGame.cs`. Contract tests: `dotnet run --project
Tools/Wp8/Wp8ContractTests`.

### WP8 lifecycle reaches a XAML title's page and grid; a held key repeats (2026-10-14)

**Lifecycle** (`Runtime/AppLifecycle.cs`). Every WP8 title gets `CoreWindow.Activated`,
`VisibilityChanged` and `CoreApplication.Suspending`/`Resuming` from `Game.Deactivated`/`Activated`.
A Direct3D/XAML title (Amazing Alex, MC4, AC Pirates) also gets what its managed shell and the
`DrawingSurfaceBackgroundGrid` did on WP8, in this order:

| leaving | coming back |
| --- | --- |
| `Activated(Deactivated)`, `VisibilityChanged(false)` | `Resuming` |
| the shell's `XamlShell.Deactivated` calls | `VisibilityChanged(true)` |
| `IDrawingSurfaceBackgroundContentProviderNative::Disconnect` | the shell's `XamlShell.Activated` calls |
| `Suspending` | `Connect(runtime host, device)`, then `Activated(CodeActivated)` |

The shell calls come from each title's decompiled `App`/`MainPage`: Alex `FusionMain.Deactivate` /
`Activate`; MC4 `SetFullVersion(true, false)` on activation only (its deactivation work is managed);
AC Pirates nothing (empty handlers). WP8 gave `Connect` a new device because the old one was lost;
the host's device never is, so the same one goes back. While disconnected the frame loop draws
nothing and takes no input, and paces itself on the host tick through `WinRtRuntime.IdleTurn`
(without that it would spin). **`Obscured`/`Unobscured` (lock screen, calls) are not raised**; MC4
pauses its game only on those, so it comes back unpaused. Log: `[wpr-wp8] lifecycle:` lines and
`[xaml] Provider::Disconnect`/`Connect`. `Disconnect` is `void`, so the r0 it logs is noise.

**Key repeat** (`Runtime/Keyboard.cs`). The host samples keys once a frame, so the runtime times
repeats: the last key pressed repeats `KeyDown` after `KeyRepeatDelay`, every `KeyRepeatInterval`,
with `WasKeyDown` true; repeats due between two frames are one event with `RepeatCount` = how many.
Another key takes the repeat over; releasing it stops repeat for good, as on Windows.
`Wp8NativeGame` reads the Windows keyboard settings (`SystemParametersInfo`), else 500 ms / 33 ms,
and calls `PumpKeyRepeat()` each frame. `CharacterReceived` is not synthesised (SDL text input
repeats on its own); a character arriving once the key has been held half the delay is marked
`WasKeyDown`.

Harness: a deactivate/activate cycle is two script verbs that set `Game.IsActive` false/true by
reflection (what `--focus-blip` does); `Script.cs` is untracked, so add them locally. Measured on
D1: all three XAML titles disconnect, draw nothing while away, reconnect and draw at 60 fps, and
Alex's Play button works after the return. Contract tests: `Suites/HostWiringSuite.cs` (XAML lifecycle) and `Suites/KeyboardSuite.cs`.

### An unimplemented C++ constructor is a defaulted import, not a shaped object (2026-10-14)

`ConstructShapedObject` and `LooksLikeVtable` are gone (the runtime convergence plan 1e). They
served one path: an imported `??0…` (an MSVC constructor from MSVCR110/MSVCP110/vccorlib/ConcRT)
with no handler, where they wrote a synthetic trap vtable into the object unless a memory probe
decided its first word was already a vptr. **It was never on the WinRT activation path**, and
those C++ classes have no `.winmd`, so 1d's generated interface table could not have replaced it.
Measured instead: zero calls across all 11 in-scope titles on D3D11 and Vulkan, and all 322 system
`??0` imports they make have handlers (the `C++ constructors` contract suite re-checks that against
whatever is installed).

Now `HostStubs.IsCxxConstructor` routes one to the ordinary default: it returns **`this`** (callers
use the constructor's return value, so 0 would be a null object), leaves the object untouched, is
counted in `DefaultedCalls` (so it is in the `summary:` line's `defaulted=[…]` and the hub report)
and traps under `WPR_WP8_TRAP_IMPORTS=1`. The old stand-in did neither of the last two. **If one
shows up, implement it** — do not bring a guessed vtable back.

The remaining shape-guessing is the WinRT stand-in for an unknown class (`CreateDiscoveryObject`):
members 1d's metadata does not describe are answered by out-parameter heuristics. Those calls are
now counted as `guessedslots=[…]` on the `summary:` line.

**Harness trap:** `gamerun`'s `WPR_DATA_ROOT` only moves where `config.json` is read from. If the
copied `config.json` still carries `DataStorePath` pointing at `%LOCALAPPDATA%\WPR`, the game runs
from the real installs and writes the **real** isolated store (whose saves the scripts did not move
aside, because they moved the scratch root's). Rewrite `DataStorePath` in the scratch copy first.

### WP8 media: byte streams, property stores, and decoding through the platform (2026-10-11)

The WP8 runtime's Media Foundation (`Src/Engine/WPR.Engine.Wp8Native/Runtime/Media*.cs`) gained three things:

* **`IMFByteStream`** (`MediaByteStream.cs`) over the host's streams - WinRT random-access streams
  and `CreateStreamOnHGlobal` IStreams, through `IHostStreams` (`HostStubs.MediaStreams.cs`) - and
  over a game's own IStream by calling its methods. **One position, shared** with the source stream,
  as Media Foundation's wrappers do. BeginRead/BeginWrite transfer at once and queue the callback
  through `InvokeLater`. Refused, recorded: a game's own `IRandomAccessStream` (its reads are WinRT
  async operations) and an IStream made over a WinRT stream.
* **`IPropertyStore` + PROPVARIANT** (`MediaPropertyStore.cs`): `MFCreatePropertiesFromMediaType`,
  `MFCreateMediaTypeFromProperties` (ours directly, a game's through its methods), `PropVariantClear`,
  `PropVariantCopy`. Key mapping is `{fmtid = attribute GUID, pid = 2}`; Windows' other answer,
  `IMediaEncodingProperties`, is not in this runtime's metadata, so it is E_NOINTERFACE.
* **Compressed audio in the media engine** through a new seam: `WPR.Engine.Audio.IAudioDecoder` +
  `AudioDecoderBackend`, declared by `caps.AudioDecoder(...)`, bridged into the runtime's
  `IMediaDecoder` by `Wp8NativeMediaDecoder`. Windows: `FFmpegAudioDecoder` over the head's ffmpeg.exe
  (temp file in, WAVE on stdout; ~30-80 ms per file). Android: `WPR.Audio.MediaCodec.MediaCodecAudioDecoder`
  (MediaExtractor + MediaCodec in the game process, since ffmpeg-kit cannot run there; ~1.5 s for
  AC Pirates' 92 s title MP3 on the S24).

**Why a run-time decoder and not install-time transcoding** (measured over the 102 ARM titles in
the library): the media engine's users (Assassin's Creed Pirates, Doodle God Blitz, FIFA 14/15,
GameMaker and Unity runners) feed it MP3; the same library ships thousands of MP3s their own code
decodes (Angry Birds), so transcoding by extension would break those; and byte-stream sources never
touch the disk. **Video is never decoded without a platform video decoder** (see the Halo section below for movies with one): a source whose container has a video stream (FIFA's
`ealogo.mp4`, Halo: Spartan Assault's `.wmv`) is refused even though its audio decodes, because a
game handed its intro's soundtrack with no picture waits for frames that never come.

**Chaining guest calls: use `MediaCom.Run` with an iterator script.** A `finish` of `Then`/`Sequence`
must never start more guest calls - the original caller is resumed straight after it, so a nested
call silently never happens. Contract tests are `Suites/MediaStreamsSuite.cs`.

### Halo: Spartan Assault runs to its mission screen (2026-10-14)

Halo (`8e162189-…`, `Game.exe`, Scaleform + FMOD + Xbox Live over D3D11) used to stop after
0 frames with `CPU exception 12 at 0xFFFFFFFE`. It now plays its four logo movies and the intro
cutscene, saves, and runs main menu → START → operation movie → operation and mission select →
armory → the "place thumbs as indicated to start" mission screen on D3D11, forced Vulkan and
forced GLES 3.0 + no DXT (that last one washed out, as D4 always is). **Gameplay needs two fingers
down at once**, which the harness cannot inject (its synthetic finger is one slot), so the mission
itself was untested then (it plays now: see "Halo plays a mission" below). Not run on a phone or the emulator. Eight separate runtime defects, each hiding
the next:

| defect | symptom | fix |
| --- | --- | --- |
| `_initterm` rejected any table over 64 KB | Halo's `__xc_a..__xc_z` has **37,303** initialisers (146 KB); none ran, and a singleton at `0x01C1A998` was called through null | cap 16 MB, both ends must be mapped; the return trap is **recycled** (37k calls fill the 64 KB trap page otherwise) |
| `OMGetRenderTargets` answered "nothing bound" | it sizes its frame from the bound target: null call at frame 244 | the context records `OMSetRenderTargets` (and `...AndUnorderedAccessViews`, GPU replay too); Get hands them back AddRef'd |
| no `ID3D10Multithread` | QI null, called anyway | on device and immediate context: Enter/Leave a recursive lock over guest threads, Set/GetMultithreadProtected |
| `IsStackAddress` knew only the main stack | `Microsoft.Xbox.User` activated on a ConcRT worker: the factory took ActivateInstance for CreateUser and wrote to address 1 | guest thread stacks are recorded at `CreateGuestThread` (static list, cleared per emulator) |
| the media engine refused every movie | **it waits for ENDED/PAUSE after an ERROR, for ever** (its EventNotify switch only logs ERROR) | movies play: see below |
| `ID3D11Texture2D::GetDesc` answered the back buffer's desc for every texture; `GetType` returned in r0 | Scaleform made a 480x800 BGRA staging twin for a 680x68 BC3 texture | resources keep their creation desc (2D/3D/buffer); GetType writes its out-parameter |
| `CopyResource`/`CopySubresourceRegion` did nothing on the context; the GPU replay turned both ends into render targets | every UI texture sampled an empty target: all-black menus with 31 draws a frame | the context copies memory (rows / 4x4 blocks / bytes); the replay copies only from a source the GPU drew into |
| `StorageFile.OpenTransactedWriteAsync` was a placeholder | "An unexpected save data error occurred", and a **0-byte savedata.bin that then fails every read** (E_BOUNDS from `DataReader.ReadUInt32`) | a `StorageStreamTransaction`: a stream over the file's contents in memory, written on `CommitAsync` |

Also `IPointerPoint.Properties` (it was a defaulted slot: touch answers, `PointerUpdateKind` from the
event). **The tell for the static-initialiser bug** is a global that `grep` cannot find a writer
for: the store sits in a dynamic initialiser the linear objdump sweep desynced past. Search the raw
`.text` bytes for the `movw` encoding instead (`4a f6 98 1?` for `#0xa998`), then find the
initialiser's address in `.rdata`.

**Movies in the media engine** (reverses "Video is never decoded" above, which stays true when no
decoder is registered). An ASF or MP4 source is offered to the platform's `IVideoDecoder`
(`VideoDecoderBackend`, bridged as the runtime's `IMediaVideoDecoder` by `Wp8NativeVideoDecoder`):
the soundtrack is drained whole (bounded 30 s) and padded with silence to the movie's length so one
clock drives both; `OnVideoStreamTick` hands out the frame due at that clock (interpolated within
the voice's ~90 ms chunk, or a movie's frames step at 11 fps); `TransferVideoFrame` scales it into
the game's B8G8R8A8/R8G8B8A8 texture through `Direct3DRuntime.WriteVideoFrame` (source and
destination rectangles, border colour). A byte-stream source goes through a temp file. Windows
plays them through ffmpeg.exe. Android plays them from sidecars: see the next section.

### WP8 movies play on Android (2026-10-16)

Halo used to sit on its first logo on a phone, for two reasons, each hiding the next:

- **No sidecars for WP8 packages.** `ApplicationInstaller.Install` and `RepatchAsync` skipped
  everything for a `ModernNative` title, `VideoCompabilityConverter.PrepareMovies` included. Both
  now run it for native titles too (through `PrepareMoviesQuietly`, never fatal); repatch still
  does no IL work and does not stamp `PatchedVersion` for them. **A WP8 title installed before this
  needs a re-patch** (Android long-press → re-patch) to get its sidecars; the launch-time auto
  repatch does not transcode. Installed WP8 titles with movies: Halo (13 `.wmv`) and Modern Combat 4
  (5 briefing `.mp4`, which MediaCodec could already read as shipped).
- **NVorbis 0.10.5 hangs in a `ReadSamples` that reaches past the end** of a stream whose first
  granule is not 0, and ffmpeg's Vorbis encoder writes exactly that (Halo's sidecars start at
  0.0507 s). `MediaCodecStream.ReadAudio` is called by the media engine's soundtrack drain on the
  guest thread, so the 30 s bound never fired and the whole game froze, with `0 host call(s)` in the
  "has not presented" report and `debuggerd -b` showing the guest thread at one managed pc. It now
  asks for no more than `TotalSamples - SamplePosition`; `StreamingSongPlayer.SubmitNext` got the
  same cap defensively (not measured to hang). Reproduced on the desktop against the pulled
  `.wpr.ogg` with a 20-line NVorbis console (hangs at 16384, 4096 and 1024-frame reads; capped, all
  finish). WP7 movies through `WprMoviePlayer` read the same method, so they were exposed too.

A **byte-stream source** is now matched back to the package file it came from before falling back to
a temp copy (`MediaEngine.FindPackageMovie`: the URL, then a file of that name, then any movie-sized
file with the same bytes, in the package or the local folder, each confirmed byte for byte), so the
decoder is handed a path with sidecars beside it. Halo opens its movies by URL and does not need it.
Log: `[wpr-wp8] media: byte-stream source … is the package file …`, and once every 2 s while a movie
plays, `[wpr-wp8] movie clock: <pos>/<len> s, chunks …, frames handed …` (a movie that never ends
says nothing otherwise).

Verified on `WPR_Verify` (Release APK, debug-signed): before the re-patch `no sidecar for
…logo_game.wmv`; the re-patch wrote all 13 pairs through `:transcode` in ~16 s; then all four logo
movies, the 34 s intro cutscene (frames on screen) and the menus, on A2 Vulkan and A1 GLES 3.1.
Once, on the very first run after the intro, a ConcRT worker stopped with `FetchProtected of 4
byte(s)` near a `savedata.bin` / `0x80070050` (ERROR_FILE_EXISTS) structure; the next two runs did
not repeat it. Not caused by this change, not chased. Not run: S24, Mali. (Chased and fixed the
same day: it is the by-value catch below.)

### Halo plays a mission, from a save or a first run (2026-10-16)

On the S24 Halo stopped as a mission loaded behind its operation movie: `CPU exception 12 at
0x00000000`, `null call to 0x00000000 from 0x004EFA49`. A first run without a save showed "An
unexpected save data error occurred", sat on CONNECTING for ever, or died with `FetchProtected`. Four
host defects, all general:

| defect | symptom | fix |
| --- | --- | --- |
| no `ID3DUserDefinedAnnotation` on the immediate context | the renderer QIs it once (unchecked) and its light pass calls `BeginEvent(L"BeginLightPass")` through the null: the stop at `0x004EFA49` (`0x4F3D78` tail-calls `this->+0x144->vtbl[3]`), on the first frame of a mission | answered as Windows does with no profiler: BeginEvent/EndEvent -1, SetMarker, GetStatus FALSE |
| a catch slot always got the thrown value's ADDRESS | `catch (Platform::Exception^ e)` is by value, so it got a pointer to the handle; its AddRef ran the exception's second vtable as code (`FetchProtected`), or the save error dialog | `HostStubs.CatchObjectValue`, as `__BuildCatchObject`: by reference the address, by value of a pointer type (`.P…`) the pointer stored there. Host-raised Platform exceptions carry the handle itself (`ThrownException.ObjectIsHandle`) |
| an exception leaving a ConcRT chore ended the worker thread | ppltasks hands a Platform exception to the task and then rethrows into MSVCR110's chore wrapper (`catch (...)`); with the worker ended, the chore's frames were never unwound and the first save's continuation chain stalled on CONNECTING | caught at the chore's return trap (`ConcurrencyRuntime.RunningChoreTraps`): cleanups run, collection canceled, the worker carries on. `throw:` lines say `caught by the ConcRT chore wrapper` |
| one handler per CoreWindow event; `IsPrimary` was "pointer id 1" | Halo subscribes Pressed/Moved/Released twice (menus and game) and only the last one heard anything; the desktop's synthetic finger (id 0x7FFFFFFF) was never primary | every handler is raised in subscription order, add_ hands out real tokens and remove_ drops one; primary = the pointer that went down with nothing else in contact |

The first save also throws `0x80070050` deliberately: it writes through
`CreateFileAsync("savedata.bin", GenerateUniqueName)` and throws when the result kept its name
(nothing to rename onto the target). That is the game's own control flow, not a host defect; it now
runs through the two exception fixes above to the main menu.

Diagnostics added: Platform exceptions in the throw history carry their HRESULT (`hr=0x80070050`);
the summary has a `d3d refused:` line (every QueryInterface the Direct3D layer said no to: a title
that ignores the HRESULT calls through the null later, possibly thousands of frames on); the file log
records WinRT `CreateFileAsync`/`CreateFolderAsync`/`RenameAsync` with their collision option; the
post-mortem lists the last `input:` deliveries and which handlers took them.

**Reproducing it on the desktop**: the phone's save, pulled read-only, in a scratch `WPR_DATA_ROOT`
whose `AppData\<id>` is a junction to the real install (`DataStorePath` rewritten, hub token
removed). START is only live once the menu has finished building, ~45 s into a harness run; earlier
taps are swallowed. Then SELECT (1140,712) twice, the mission loads ("initializing simulation"),
"place thumbs" wants two fingers: the harness's `Script.cs` has local `down/move/up <id> x y` verbs
that inject straight into the WP8 runtime (`WinRtRuntime.InjectPointer`, reflection), at
(185,590) and (1095,590), pressed half a second apart. With the annotation refused (a temporary
switch) the same run stops at exactly `null call to 0x00000000 from 0x004EFA49`.

Contract tests: `Suites/CatchObjectSuite.cs`, `Suites/PointerSuite.cs`, the throwing chore in
`ConcurrencyRuntimeSuite`, the annotation in `Direct3DStateSuite` (2244/2244).

| cell | phone save: menu → operation A → mission A-1 → two thumbs → play | first run, no save |
| --- | --- | --- |
| D1 D3D11 | ✅ gameplay, Spartan moves with the left thumb, 2,300 BeginEvent calls | ✅ main menu, `savedata.bin` 2424 bytes |
| D2 Vulkan | ✅ gameplay | not run |
| D4 GLES 3.0 + no DXT | ✅ gameplay (washed out, as D4 always is) | not run |
| A1/A2, P1–P3 | not run (coordinator tests the S24) | |

**Open, seen on every driver:** in play some objects (crates, Covenant units, parts of the HUD
weapon panel) draw as solid black silhouettes. Not chased. WP8 regression (11 titles, Terraria /
Halo / GTA excluded, 32 s from clean saves, combine vs this change, D3D11): the same screens.

Diagnostics added on the way, all opt-in: `WPR_WP8_IMPORT_TRACE=N` (first N imports, critical
sections skipped), `WPR_WP8_METHOD_TRACE=<text>` (host vtable calls whose name contains it,
AddRef/Release skipped, first 20,000), a wild branch's report now unwinds from lr, every throw in
the history says where it came from, and the run summary lists where each live guest thread is
parked (`threads:`) - a stall without a failure leaves nothing else. The harness registers the
desktop video decoder with the audio one when `WPR_FFMPEG` is set.

Regression, D3D11, combine 7985de7a vs this branch (11 titles, `wp8-shots.ps1`): no screen got
worse; MC4 now reaches its "touch the screen" title (was the Havok logo at 30 s), Star Wars its
daily-reward menu, AC Pirates further into its intro. Runs where the harness window lost focus
(`lifecycle: suspended`) were repeated. Contract tests 2214/2214 (`StartupAndStacksSuite`,
`Direct3DStateSuite`, transacted writes in `WinRtClassesSuite`).

### The WP8 GPU replay honours scissor rectangles (2026-10-16)

Reported as "Angry Birds Stella's achievements overlap the title when scrolled". Rovio's menus
(Stella's Achievements board, `4d1a2f37-…`) clip their scrolling lists with
`RSSetScissorRects` + a rasterizer state with `ScissorEnable`, called every frame. The context
slot (45) had no handler and the replay forced `ScissorTestEnable = false`, so rows drew over the
board's title at the top and past its frame at the bottom. Now `Direct3DRuntime.ScissorRect`
keeps the first rectangle (`RSGetScissorRects`, slot 96, hands it back), `GpuDraw.Scissor`
carries it, and `Wp8GpuReplay` sets `ScissorRectangle` (clamped to the target) only when the
draw's raster desc has ScissorEnable (byte 28) **and** a rectangle was set; an empty rectangle
draws nothing. No rectangle with ScissorEnable on is treated as no clip (D3D would clip
everything; no title relies on that). The `WPR_WP8_GPU=0` draw list still ignores scissor.
The tell for this class: `rasters=` in the `guest fps` line, and `WPR_WP8_METHOD_TRACE=Scissor`.

Repro in `gamerun` (1280x768 display coords): `24 hold 535 715 0.2` (trophy button on the main
menu), then `30 swipe 640 550 640 250 3` and a `shot` mid-drag. Verified D1–D4; contract test in
`Direct3DStateSuite`.

### The WP8 runtime must not allocate per guest call (2026-10-14)

Reported as "Assassin's Creed Pirates (`40ae56ef-…`) is really slow on my phone". On the S24 the
log showed ~370 gen0 GCs a second on the menu, ~2,700 a second during a ~30 s 0-fps stretch after
PLAY, and `managed=` climbing ~60 MB/s in play to 2.9 GB (6 GB in an earlier run), with 316 MB free.
**The desktop reproduces all of it, so measure there first**: `scratchpad/gamerun` against a scratch
`WPR_DATA_ROOT` (rewrite `DataStorePath` in the copied config.json), skip the intro, tap PLAY.
`GAMERUN_NOFOCUS=1` stops a focus change on the desktop suspending the game mid-run, and
`GAMERUN_ALLOC=1` prints GC allocation ticks by type every 5 s. For stacks,
`dotnet-trace collect --profile gc-verbose` and aggregate the `GCAllocationTick` stacks with
TraceEvent. **The MP3 decode is not the stall**: MediaCodec took ~1.5 s, ffmpeg less.

Measured on the desktop (D3D11), before and after:

| | before | after |
| --- | --- | --- |
| allocation during the post-PLAY load | ~340 MB/s (memcmp/memcpy/strlen/strcmp arrays) | ~0 (gen0 49→50 over 25 s) |
| allocation in play | ~190 MB/s, much of it LOH `int[]` | ~20 MB/s |
| private bytes at 130 s | 7.0 GB and climbing ~100 MB/s | 2.0 GB, flat |
| `managed=` in play | 1.4 → 5 GB | 330-400 MB |

The causes, each a pattern to avoid in this runtime:

- **An array per guest-memory access.** `ArmEmulator.ReadMemory(long, int)` returns a new `byte[]`,
  and `ReadUInt32`/`WriteUInt32` (every critical-section word), memcmp (both sides at the full
  count), memcpy, memset, strlen and strcmp all went through it. `IArmCpu.MemReadInto`/`MemWriteFrom`
  (span overloads; the dynarmic engine reads straight into the span, Unicorn keeps a default) and
  `ArmEmulator.ReadMemory(long, Span<byte>)`/`WriteMemory(long, ReadOnlySpan<byte>)`/`CopyMemory`
  now serve those. **Use the span forms on any path the guest can hit per call.**
- **Per-draw index resolution.** `FrameCapture.Snapshot` resolved every draw's indices into a new
  `int[]` plus a `byte[]`, each frame. They are now cached on the index buffer `Resource` by
  `Version`, the twin of the vertex `Snapshots` cache; constant-buffer copies likewise
  (`ConstantCopy`). Both rely on Map/UpdateSubresource bumping `Version`.
- **Two unbounded lists.** `ArmEmulator._insideDeferred` recorded every import a deferred callback
  made, to name them when it returned. A callback that never returns (a worker loop) grew it for
  ever, a 4M-entry list in a heap dump. It now stops at 91 (more are never printed). And
  `Direct3DRuntime.Unhandled` keyed its once-only list on the call's registers, so each call was a
  new entry found by a linear scan; it is once per slot now.
- Smaller ones: closures allocated on the uncontended path of `EnterSection`/`WaitAny` (C# creates a
  method's closure class at its top, so the slow path moved to its own method), hex-string keys per
  draw for blend/depth/raster/sampler state (an identity-keyed cache sits in front), and a
  `Vector4[]` per draw in `SetConstants`.

**The ~30 s load after PLAY is still there, and it is CPU, not GC.** Profiled with
`dotnet-sampled-thread-time`: 54% guest code, ~40% in `wprcpu_read`/`wprcpu_write` (big memcpys
through a host buffer, ~3M host calls a second; the S24 makes the same number). Halving that needs a
native guest-to-guest copy in `wprcpu` (a rebuild of `wprcpu.dll` and the Android `.so`), not more
managed work. The `underruns=` counter climbing ~60/s is a voice left playing with nothing queued,
on the desktop before and after; it is not this.

Contract tests: 2,177 passed. No `ApplicationPatcher.Version` bump and no reinstall.

### WP8 achievements and leaderboards reach WPR through one door (2026-10-14)

Every in-scope WP8 native title ships `Microsoft.Xbox.winmd` and calls Xbox Live from its **native**
code. That covers Rovio, Gameloft MC4, EA MONOPOLY, and Ubisoft's Spark2 through `LuaMobileSDK.dll`.
None uses a system of its own, and MC4's managed page only calls `FakeInitXBLUser`. So there is one
path:

```
guest -> XboxRuntime.cs / XboxData.cs / XboxAttributes.cs  (WinRT slots)
      -> IXboxLiveHost  (Runtime/, engine-free)
      -> Wp8NativeXboxHost
           achievements -> SignedInGamer.AwardAchievement -> achievements.db, toast, OnlineBackend.Progress
           leaderboards -> OnlineBackend.Leaderboards (WPR Hub)
```

The XNA award path does the rest: the "already earned, no toast" rule, `EarnedOnline` and the upload.
`ApplicationLaunch.Start` creates the host for every `ModernNative` launch. `PlaytimeTracker` runs
there too, so WP8 playtime and presence reach the hub with no extra work. Catalogue keys are the
numeric XLAST ids (see the memory note on that).

| family / title | achievements | leaderboards | verified |
| --- | --- | --- | --- |
| Rovio: Classic, Rio, Seasons, Space, Star Wars, Star Wars II, Stella, Amazing Alex | `GetAchievementsAsync` at boot; unlock by XLAST id | `PostResultAsync` (Max); board list from the SPA in `xbl.dll`, or for Seasons / Star Wars / Star Wars II from `ServiceBoards` | **Classic live**: level 1-1 cleared, 31140 posted to boards 1 and 2, read back in its own Total score screen beside another hub player. Achievements screen draws the catalogue |
| EA: MONOPOLY Millionaire | same | SPA: board 4 `Score`, **Sum**, i.e. Add | **live**: starting Pass 'n Play unlocked 17. Toast `5G - Multi-Millionaires!`, `achievements.db` `IsEarned=1 EarnedOnline=1`, hub `user_achievements` row with playtime |
| Ubisoft Spark2: AC Pirates | through LuaMobileSDK, XBL network | SPA: board 1 `BOUNTY`, Max; names `LeaderboardAttribute` | **live**: sign-in, achievements menu, leaderboard read reaches the hub (1 row). The row is not drawn (see gaps) |
| Gameloft: Modern Combat 4 | `GetAchievementsAsync(max 20)` | SPA: `LOCAL_XP`/`ONLINE_XP`, **Min** | reads only. Its menu took no synthetic input, so no unlock or post was seen |

What this needed (`9ee14875`, `46248967`):

- **`GetLeaderboardsAsync` answers the title's boards**, read from the gameconfig inside its Xbox SPA
  (`XboxTitleConfig`: `XDBF` module, gzipped UTF-16 `<GameConfigProject>`, `StatsView` id, en-US
  name, rating aggregation). It used to answer an empty list, and every Angry Birds leaderboard screen
  then stopped on "Sorry, we're not sure what happened" without asking for a board. Boards the title
  has posted to (`KnownLeaderboards`, persisted) are always added to the list.
- **Seasons, Star Wars and Star Wars II defined their boards on the service, not in the SPA
  (2026-10-16).** Their gameconfig has no StatsViews, so `XboxTitleConfig.ServiceBoards` supplies
  them, keyed by the gameconfig's Xbox `titleId` (Seasons `0x314C1392`, 21 boards; Star Wars
  `0x314C138A`, 9; Star Wars II `0x314C1396`, 17). Ids come from the exe's id-to-name table, the same
  `movw/movt "NAME"; movs r3, #id; bl insert` shape as the achievement table; they step by 16 and do
  not follow display order (Seasons TOTALSCORE is 0, episode 1 is 224, episode 19 is 304; Star Wars
  TOTALSCORE is 32, Tatooine 0). The Lua menu (`leaderboardOrder` in `scripts/xboxlive/leaderboards.lua`)
  shows a board only if the list returned its id, and ignores ids it does not know. **Star Wars II
  draws `LeaderboardName`** cut by `string.sub(name, 1, -19)` (Bird Side) / `-18` (Pork Side), so its
  names carry an 18/17-character side suffix; the other two draw their own artwork. **Amazing Alex
  never calls `GetLeaderboardsAsync`**: it reads board 0 ("Most Stars Collected") directly, which
  already worked. Seasons' Lua key (for checking these) is `zePhest5faQuX2S2Apre@4reChAtEvUt`; it is
  not in the exe as a string, it was found by scanning a guest heap dump for a 32-byte window that
  decrypts the first block to the 7z magic. Verified on D3D11 against a scratch hub: Seasons' list,
  an empty Total Score board, a won level posting to 304 and 0, both read back beside another
  player; Star Wars' and Star Wars II's lists and Total Score with a row; Classic and Space unchanged.
- **`LeaderboardAttribute` + `PostResultsAsync`**: the class was improvised and the call posted
  nothing. The game's own attribute vector is now read through its vtable. The rating is the
  lowest-numbered numeric column and the others travel as columns.
- **Host board rules** (`Wp8NativeXboxHost`, state in `Online/wp8-leaderboards/<productId>.json`):
  - Add is summed locally and the total sent, because the hub keeps a player's best.
  - Min goes to `BestTime<id>:0` (lower wins); everything else to `BestScore<id>:0`. The aggregation
    is remembered, so a read before any post in this run names the right board.
  - **A 0 to an Add/Max/Min board is not sent.** Star Wars II posts 0 to every board at each boot from
    a fresh save.
- **Spark2 `IsInternetAvailable` is true when an Xbox Live host is attached.** AC Pirates gates its
  Achievements and Leaderboard menus on it alone.
- The run summary gains an `xbox:` line: activations, `SignInAsync`, every achievement/leaderboard
  call with its ids. Read it first when a title's online features look dead.

Contract tests: `Suites/XboxLiveSuite.cs`, covering the guest surface, the SPA reader, and
`Wp8NativeXboxHost` against an in-memory store, toast, progress and leaderboard service.

**Open:**

- **MC4's Min.** MC4 posted nothing in any run, so the rule could not be decided from what it does.
  Its SPA puts Min on an XP column. If MC4 does post with Min, XP lands on a lower-wins board.
  `BoardKey` is the one place to change.
- **AC Pirates' leaderboard rows are not drawn.** The rows arrive, but each row's `get_Attributes` is
  an empty view. The game probably asks `GetLeaderboardAsync` for attribute ids and draws those.
- **MONOPOLY's `Score` board (Sum)** and **Classic's unlocks** were not reached live. Classic's
  achievements are by bird or star count.
- **Service-defined boards not run on a phone.** The Seasons / Star Wars / Star Wars II tables are
  shim data and not platform-specific, but were verified on the desktop only. A title with no SPA
  boards that is not in `ServiceBoards` still shows only the boards it has posted to. Of the
  installed Rovio titles every other one defines its boards in its SPA (Classic 13, Rio 13, Space 23,
  Stella 1).
- ~~**Hub board names**~~: fixed 2026-10-23, see "WPR Hub leaderboards have names" below.
- **Harness, desktop in use:** a scripted run must suppress focus activation. Otherwise a focus loss
  suspends the WP8 guest (`lifecycle: suspended`, fps ~0). The xbl harness copy calls
  `XnaBackend.SuppressFocusActivation`.

### WPR Hub leaderboards have names (2026-10-23)

A board a game created used to be named its raw key (`BestScore1:0`, `BestScoreLifeTime:0`). Now:

- **WP8, native and managed**: `Wp8NativeXboxHost` (given the install folder by `ApplicationLaunch`)
  reads the title's Xbox SPA with `XboxTitleConfig` and sends `XboxTitleConfig.HubName(board)` with
  every read (`EnsureBoard(…, boardName)`) and post (`Submit(…, boardName)`). An internal
  identifier (Seasons' `TOTALSCOREEPISODE3`, Star Wars' `HOTH_1_LBNAME`) is made readable ("Total
  Score, Episode 3", "Hoth 1"). **Managed WP8 packages carry the same SPA** (measured over the
  library: Bad Piggies, Gravity Guy 2, Mahjong, Minesweeper, Solitaire, Temple Run 2, Fruit Ninja
  2013, MC4… in their own managed DLL), and they reach the same host, so they are named too. The
  managed facade's `GetLeaderboardsAsync` still lists only boards posted to; that list is game-visible
  behaviour and was left alone.
- **WP7 XNA**: no proven XLAST mapping exists (54 of 59 XlastConfigurations hold achievements only),
  so WPR sends no name and **the hub makes the fallback from the key** (`LeaderboardNames::fallback`:
  "Best score", "Best time (recent)", "Best score (mode 2)", and for WP8 keys "Best score (board 2)").
  Putting the fallback on the hub covers old clients and the backfill with one rule.
- Sort order is already carried by the key (`BestTime*` = lower wins), so no sort is sent.

**Hub** (branch `leaderboard-names`, from main; independent of `title-icon-uploads`): column
`leaderboards.name_source` = `key | fallback | game | curated`. A game's name fills only `key` or
`fallback`, never replaces another game name, and never a `curated` one (admin define/update, the
definitions importer, and an admin-named board that was later undefined). `POST …/boards` takes
`names: [{key, name}]`, `POST …/scores` takes `scores.*.name`; rate limits and the
25-undefined-boards cap are unchanged. **Deploy is `php artisan migrate` alone**: a migration
gives every board still named by its key the fallback, any board still on its key when a game next
uses it gets the fallback then, and the client upgrades it to the real name on the next read or
post. Admins rename boards from the game pages. Admin pages show a "from the game" / "from the
key" pill on undefined boards.

Verified 2026-10-23 against a scratch hub (old rows inserted before the migration): Angry Birds
Classic's post created `BestScore1:0` "Total Score"; a read upgraded the backfilled `BestScore2:0`
("Best score (board 2)", given by the backfill then done by a command, now a migration) to "Poached Eggs Total Score"; an admin-named undefined `BestScore3:0` kept
its name; Fruit Ninja (WP7) got "Best score" (backfill) and "Best score (mode 2)" (new). Tests: hub
`LeaderboardNamesTest` (7), WP8 contract `xbox live` (names checks pass; the 3 toast checks there
fail on 0.2.07 already, since a7a69b20 moved unlock toasts in-game).

### Managed WP8 titles reach Xbox Live too, and a package's own XNA layer runs (patcher v45, 2026-10-19)

41 packages in the library are **managed** WP8 titles that call `Microsoft.Xbox` from C# (Gravity
Guy 2, Bad Piggies, Rayman Jungle/Fiesta Run, Lara Croft Relic Run, the Microsoft casual games...).
They used to get WinmdStubber's do-nothing stub: sign-in never completed and achievements went
nowhere. Now they go through the same door as native titles:

```
game C# -> Microsoft.Xbox (WPR.Framework.Xbox) -> XboxLiveBackend.Host (WPR.Engine.XboxLive)
        -> Wp8NativeXboxHost -> achievements.db / WPR Hub
```

| project | builds as | what it is |
| --- | --- | --- |
| `Src/Engine/WPR.Engine.XboxLive` | `WPR.Engine.XboxLive` | `IXboxLiveHost` + records (moved out of `Wp8Native/Runtime`; the probe tools compile the file directly) and `XboxLiveBackend`, the per-launch slot `ApplicationLaunch` fills with the same host it gives the native runtime and clears in `ResetWprSingletons` |
| `Src/Core/WPR.Framework.WinRT` | `System.Runtime.WindowsRuntime` 4.0.0.0 | real WinRT async objects over `Task` (`IAsyncAction`, `IAsyncOperation<T>`, handlers, `AsyncStatus`), `WindowsRuntimeSystemExtensions` (await/AsTask), `EventRegistrationToken`/`WindowsRuntimeMarshal`/`AsyncInfo`, and `Size`/`Point`/`Rect`/`Color` with properties |
| `Src/Core/WPR.Framework.Xbox` | `Microsoft.Xbox` 255.255.255.255 | the whole `Microsoft.Xbox.winmd` surface (all 23 copies in the library are identical, 365 members), .NET's projection applied |

Things that are load-bearing:

- **WPR's own assemblies bind before an install folder** (a collectible game ALC has no `Load`
  override, so the default context is asked first). That is why the facade replaces the stub even in
  installs made before it existed, and why the version must be 255.255.255.255: the TPA binder wants
  available >= requested.
- **Setters are methods named `put_X`**, not C# setters: WinRT metadata names them so, and game IL
  calls `put_Completed`. Getters are ordinary properties.
- **`Completed` fires exactly once**, at once on the setting thread if the operation already
  finished (WPR answers most calls synchronously), else on a pool thread.
- **A null async source awaits as completed-with-default** instead of throwing: every null one comes
  from a stub of a component WPR does not implement.
- **Patcher v43** retargets WinRT metadata's `mscorlib, Version=255.255.255.255` to the 4.0.0.0
  facade (the stubber used to copy it into every stub, which .NET cannot bind; Mahjong and Solitaire
  hit it 9 and 11 times). **v45** rescopes the async family and the mscorlib interop types
  (`WinRtProjectionTypes`) to `System.Runtime.WindowsRuntime`, normalises references to it to 4.0.0.0,
  and WinmdStubber no longer stubs `Microsoft.Xbox` (`Implemented`).
- **v45 also stops the patcher stealing a package's OWN implementation.** Its by-name rules
  (`WprFrameworkXnaTypes`, `Patches`, `WinRtProjectionTypes`) used to fire whatever assembly a ref
  pointed at. Gravity Guy 2's `SharpDXCommon.dll` ships its own managed `Microsoft.Xna.Framework.Game`,
  `Texture2D`, `GraphicsDevice`... over SharpDX, and its refs were swapped to WPR.Framework.Xna's, so
  the game's code never ran (`MissingMethodException ... Game.get_Content()` returning a SharpDX
  type). A framework-namespaced ref scoped to a package assembly that is not a framework copy
  (`PackageOwnsScope`, `FrameworkAssemblyName`) is now left alone; log `[own-impl]`. Game-specific
  redirects (`SWTheBattleForHoth.*`) are not framework-named and still apply. Measured over the
  307-package library: a package type name collides with a patcher name **only** in Gravity Guy 2.

**Audited, nothing stubbed that could run:** all 127 kinds of `.winmd` in the library carry zero IL
bodies and every sibling `.dll` is native ARM, so WinmdStubber only ever replaces metadata + native
code. The only bodies the patcher empties are the two documented fixups (Eazfuscator caller check,
Montezuma's options dialog). Same-named shadowing that remains is by design: framework copies
(`System.*`, `Microsoft.Phone` in Pool Pro) and `Newtonsoft.Json` (42 packages bind WPR's newer build).
`FNWP72.dll` (Fruit Ninja's own game assembly) was committed in the Windows head from 0.1.0
and copied to the output, unused (not in deps.json); removed 2026-10-19.

Verified 2026-10-19, desktop D3D11 harness, scratch data root: Gravity Guy 2 signs in as the WPR
player (`[wpr-xbox] SignInAsync -> <gamertag>`), its own SharpDX `Game` is constructed, and it then
stops on `Windows.ApplicationModel.Package.Current` (an empty per-game shell). **It still draws
nothing**: past that it presents through SharpDX's native D3D11 interop, which WPR does not host.
Minesweeper, What In The World?, Mahjong and Solitaire reach the same screens as on main (black for
the SharpDX/Arkadium ones), with the `mscorlib 255` and `put_Completed` failures gone; Angry Birds,
Carcassonne and Flowerz unchanged. Tests: `XboxLiveFacadeTests` (16), shim suite 261/261, WP8
contract tests 2296/2296, `BackendIsolationTests`, the WP8 probe builds, android legs build.
Not run: Android, any phone. Not handled: the 3 titles compiled against raw WinRT types for user
messages/multiplayer/`SignedOutEventArgs.ErrorCode`/`LeaderboardRow.Attributes`; multiplayer and
marketplace purchases fail as an unreachable service (marketplace READS answer an empty marketplace
since 2026-10-19, below); `GetLeaderboardsAsync` lists boards posted to (no SPA read
for managed titles); `System.IO` WinRT stream/storage extensions and `WindowsRuntimeBuffer`.

**v46 (same day): Gravity Guy 2 boots to its game loop.** `Windows.ApplicationModel.Package` (`Current.InstalledLocation.Path` = the install folder), `Windows.Storage.StorageFolder` (path only), `Windows.Graphics.Display.DisplayProperties` and the real `ResolutionScale`/`DisplayOrientations` enums joined WPR.Framework.WinRT and `WinRtProjectionTypes` (the per-game shells had a class for an enum and a Package with no Current). `WinRtHost` holds the per-launch answers, from `Wp8Display`. WP8's static `SystemTray.IsVisible`/`Opacity`/`BackgroundColor`/`ForegroundColor` were added beside the attached ones. Gravity Guy 2 now builds MainPage, signs in and calls `SharpDX.Toolkit.Game.Run`, with no exception, and then never ticks: the toolkit's `GamePlatformWP8` registers itself through `DrawingSurfaceBackgroundGrid.SetBackgroundContentProvider` and waits for `Connect(host, device)` and a per-frame `PrepareResources` + `Draw(device, context, renderTargetView)` over native Direct3D 11. Rendering it needs either real D3D11 behind SharpDX (desktop only) or managed SharpDX facades over FNA (both heads; ~290 members, its 6 `.cso` shaders and 67 of the toolkit's carry the Aon9 Direct3D 9 chunk MojoShader runs, content is `.dds` + `.tkfnt`).

### Gravity Guy 2 plays: a title's own SharpDX runs on a host Direct3D 11 (2026-10-19)

**WPR does not shim SharpDX. It implements what SharpDX calls**, so the title's own `SharpDX*.dll`
run as shipped and any other SharpDX title needs nothing per title. (A first version replaced
SharpDX with six managed facades bound per package by the patcher; that was dropped the same day,
and no patcher change remains.)

SharpDX's only way into native code is the **`SharpDX.WP8`** WinRT component every such package
ships (native ARM): each static method of `SharpDX.WP8.Interop` returns the address of a system
export as an **int** (`D3D11CreateDevice`, `CreateDXGIFactory1`, `CreateFile2`/`ReadFile`/...,
`XAudio2Create`, `MF*`), which SharpDX turns into a delegate. Everything after that is plain IL
calling COM vtables. So:

| project | builds as | what it is |
| --- | --- | --- |
| `Src/Core/WPR.Framework.SharpDXWP8` | `SharpDX.WP8` 255.255.255.255 | `Interop`, answering each export with `HostExports.Address(name)`. Binds before the install folder (the `Microsoft.Xbox` facade trick); WinmdStubber leaves it alone (`Implemented`) |
| `Src/Engine/WPR.Engine.HostDirect3D` | `WPR.Engine.HostDirect3D` | the exports, and Direct3D 11 / DXGI as host COM objects whose vtable slots are `[UnmanagedCallersOnly]` C# |

```
title C# -> its own SharpDX / SharpDX.Toolkit (unmodified)
         -> SharpDX.WP8.Interop (WPR) -> HostExports -> HostDevice / HostContext (record)  -> GpuFrame
DrawingSurfaceBackgroundGrid.SetBackgroundContentProvider -> DrawingSurfaceHost: Connect, then per
  host frame PrepareResources + Draw(device, context, back buffer RTV) -> Wp8GpuReplay.Replay + Present
```

Things that are load-bearing:

- **The addresses must fit an int.** `NativeCode` writes jump thunks into memory allocated below
  2 GB (`VirtualAlloc`/`mmap` with a hint), written once and then made executable, x64 and ARM64.
  Unimplemented vtable slots are reporter thunks: `[wpr-d3d] unimplemented: ID3D11DeviceContext1::X
  (slot N)` once, E_NOTIMPL. A refused QueryInterface is logged the same way. **Read those two
  lines first** when a new SharpDX title misbehaves.
- **Signatures are SharpDX's delegate declarations**, not the Win32 headers: `ReadFile` returns a
  4-byte BOOL with `out int`, `SetFilePointerEx` takes a `SeekOrigin`, `CreateFile2`'s dispositions
  are `NativeFileMode`. Relative paths are the package's, Windows separators converted (`HostFiles`).
- **The context holds a reference to everything bound** (`HostObject.Hold`), as Direct3D does.
  Cocos2D's EAGLView binds a depth-stencil state and disposes its wrapper at once; without the
  reference the state was freed while bound and `GetDescription` NRE'd inside `setOpenGLView`.
  States are deduplicated by content, as Direct3D does.
- **The provider is driven through its own native face**: a managed provider answers
  `ICustomQueryInterface` for IID `262c1892-...` (IDrawingSurfaceBackgroundContentProviderNative),
  and its slots 3/5/6 are Connect / PrepareResources / Draw, exactly what the phone called.
  `ApplicationLaunch` sets `DrawingSurfaceBackgroundGrid.ContentProviderSet` per launch; the host
  runs as `WPR.Xna.Rhi.EmbeddedGameLoop`, drawn by `SilverlightGame` under the page.
- **Recording is the WP8 native runtime's**: `GpuFrame` and `Wp8GpuReplay` (Aon9 shaders, raw
  D3D11 descs). Buffers' arrays are replaced, never written in place. The back buffer is the
  portrait phone screen (`Wp8Display`, 768x1280); a landscape page is presented
  `PresentTurn.Clockwise` (GG2 draws for LandscapeLeft; the native runtime's titles need the other
  turn).
- **Not provided**: XAudio2 (`XAudio2Create` answers E_NOTIMPL and is logged; GG2 does not use it,
  its sounds are XNA `SoundEffect`s), Media Foundation (each `MF*` export E_NOTIMPL, logged by name),
  `CoCreateInstanceFromApp` (WIC; logged with the CLSID), `LoadPackagedLibrary`.

Three general fixes it needed, each of which applies beyond this title:

| fix | symptom here |
| --- | --- |
| `Wp8GpuReplay.Ordered`: a **denormal** constant is read as the integer it is (1 for a bool). A C# `bool` copied byte for byte into a cbuffer is the float 1.4e-45; GPUs flush it, so `cmp -c0.x, colour, textured` took the untextured side. The smallest normal float fixed D3D11 but not GLES (medium precision flushes that too). `WPR_WP8_CONSTANT_INTS=0` turns it off | every CCSprite a white box of its vertex colour |
| `Touch.FrameReported` is raised (`SilverlightGame.PumpTouchFrames`), every finger, once a tick while anything subscribes, with `Up` for a finger that vanished without a Released sample; `TouchFrameEventArgs.GetTouchPoints(element)` is relative to the element; `UIElement.RenderSize` exists | the menu took no tap at all (Cocos2D's only input path); then PLAY stayed pressed for ever |
| Marketplace READS (`EnumerateOffersAsync`, `GetReceipts*`, `GetAssetsAsync`) answer an empty marketplace (`XboxHub.EmptyMarketplace`); purchases still fail. On a phone the reads succeeded, and a failure path can assume a screen that does not exist yet | `DLCManager` raised `LastErrorChanged` with no subscriber: NRE out of `Initialize`, before `LoadContent` |

Verified 2026-10-19, desktop harness, scratch data root: main menu (clone tube, SHOP / OPTIONS /
PLAY, Achievements, Leaderboards), PLAY taken, the first level running with its "Tap once to jump"
tutorial, on **D1 D3D11, D2 Vulkan and D4 GLES 3.0 + no DXT**. Contract suite `HostDirect3DSuite`
(exports, device creation, state dedupe and lifetime, the file calls). **Not run on Android or a
phone.** Not done: **music** (the title's two `.mp3` songs are skipped: WPR's song player is Ogg
and the installer transcodes `.wma` only), mip levels below the top one. `sfx_teleport.wav` is
missing from the package itself (the title catches it). Re-verified 2026-10-21 on D1/D2/D4 with the
harness script `14 hold 680 330 0.2; 26 hold 400 240 0.2`. **A PLAY tap at ~25 s does nothing, on
every build back to this one**: that is the game's own menu state, not a regression; press it early.

### Microsoft Mahjong, Minesweeper and Solitaire play (patcher v47-v49, 2026-10-21)

Arkadium's three WP8 titles (`3533f0fe-…` Mahjong, `c430a852-…` Minesweeper, `282592d5-…`
Solitaire) run their own SharpDX engine (`Arkadium.SharpDXEngine.WP8`) behind a
`DrawingSurfaceBackgroundGrid`, with Silverlight menus. Verified D1 D3D11: Mahjong's tutorial pair
matched, Minesweeper's tutorial and first tap, Solitaire's Klondike dealt with its tutorial dialog.
Microsoft Bingo was not looked at. What it took, roughly in the order each one hid the next:

| fix | without it |
| --- | --- |
| v47: WinRT-flagged refs stripped in the patcher; v48: a typeref into Silverlight's `System.Windows` that `Patches` does not list binds the shim of the same name (`SilverlightShimTypes`, by simple name); v49: the same, re-run for the new shim types below | `PlatformNotSupported` / `TypeLoadException` in `App` construction and on every tap that met a missing type |
| `DrawingSurfaceHost.Attach` connects inside `SetBackgroundContentProvider`, and the device, runtime host and back buffer are kept for the launch (`ResetDevice` at teardown) | Arkadium reads the provider's device on the next line; a second Connect with a "new" device made it reload everything and fail |
| `CompositionTarget.Rendering` is raised once a frame (`SilverlightGame.Update`, after the storyboard clock) | Arkadium's engine is ticked ONLY from a Rendering handler (`EngineThread.RequestUpdate`): the board recorded 0 draws a frame |
| `Host.Content.ScaleFactor` is the WP8 display's (160) for an `AppPlatformVersion` 8.x title (`SilverlightHostContent.PlatformScaleFactor`), 100 for WP7 | the engine sized itself for 480x800 and drew in a corner of the 768x1280 surface |
| a frame painted only by the background grid gets the page composited OVER it (no clear) | no XAML HUD, tutorial text or buttons over the board |
| the grid's pointers are turned into the phone's portrait frame (inverse of the present turn), and a `LandscapeLeft` page is presented with the anticlockwise turn (`EmbeddedGameLoop.PageLandscapeLeft`) | taps missed every tile; Minesweeper's board drew upside down |
| a star Grid's desired size is its content's, not its share of the space offered | a `VerticalAlignment="Top"` banner (#CC000000) filled the screen and took every tap |
| `Dispatcher.BeginInvoke` is QUEUED when the Silverlight host is attached and drained once a tick (after the page's and embedded game's updates, before the FrameworkDispatcher fallback) | Solitaire posts `SetupGameMode` and then subscribes to the event it raises; inline, it fired with no subscriber and the game sat on its preloader. Drained at the top of the tick instead, Cocos2D's posted `FrameworkDispatcher.Update` went uncounted and the fallback pumped twice. `SilverlightAppHost` also drains it once before navigating to the start page, as the phone queued that navigation behind the app's startup posts: without that, Galactic Reign's MenuPage NRE'd in its view model and the screen stayed black |
| children of a writable IList property are added to its collection (XamlReader) | `ApplicationBarBehavior.MenuItems` was set to an item: InvalidCast in GamePage's Loaded, engine never initialised |
| bindings hook every object along a dotted path; `Converter`, `ConverterParameter`, `FallbackValue`, `TargetNullValue` | `Model.ScreenSize` updates never arrived (text boxes 0 wide); Boolean-to-Visibility binds failed |
| `RichTextBox` + `System.Windows.Documents` (Paragraph, Run, Span, Bold, LineBreak, InlineUIContainer), drawn as one TextBlock per paragraph (inline pictures leave a gap, run colours are lost); `DiscreteDoubleKeyFrame`; `TransformGroup` content; `Microsoft.Devices.Sensors` resolvable from App.xaml | Minesweeper's tutorial control, its difficulty tiles and its first tile tap threw |

Also on the way: an XAudio2 2.8 host for SharpDX titles (`HostXAudio2`, voices on the WP8 runtime's
`XAudio2BufferQueue`), the D3D11 context's tail slots corrected (ClearState is 110, 134 slots),
Panorama/Pivot headers from their HeaderTemplate, ItemsControl containers with ContentTemplate.

**Diagnosing these titles:** the harness's `peek` verb (local `scratchpad/gamerun/Script.cs`) reads a
member path from a page in the tree or a static type: `peek GamePage.Game._gameBoardView.IsTilesAppeared`,
`peek @Arkadium.GameEngine.Core.EngineManagers.Display.ViewState`. A stuck Arkadium page is nearly
always an engine flag (`IsRestoreDeviceNeeded`, `IsPreloaderVisible`, a `ReadyToSetGame` with no
subscriber), and `[Loaded] handler … threw` goes to stdout, not the per-game log.

Regression, D1, same scratch root: Carcassonne (reinstalled at v49), Flowerz (menu, Play page,
board), Cut the Rope Exp., Angry Birds, Galactic Reign, Big Buck Hunter Pro, The Game of Life,
FC Rocket and Little Acorns reach the same screens, with the same composite/no-composite answers as
"Who draws a mixed-mode page"; Gravity Guy 2 plays on D1/D2/D4. Shim tests 269/269 (the new
`ArkadiumTitlesTests`), WP8 contract tests 2308/2308.

**Follow-ups, same day (patcher v51):**

- **`HostBuffer` reuses replaced arrays.** Data is still replaced rather than written in place (a
  recorded draw keeps the array it was recorded with), but a replaced array is retired and reused
  once the frame that could have recorded it has been replayed (`HostBuffer.FrameReplayed`, called by
  `DrawingSurfaceHost` after each replay); the replay first drops any stream it cached by that array's
  identity (`Wp8GpuReplay.ForgetStream`, through `HostBuffer.Recycling`). An Unmap that changed nothing
  keeps the array. `managed=` at frame 1201: Minesweeper 1058 MB -> 125 MB, Mahjong 447 -> 138 MB at
  601; Gravity Guy 2 flat at ~120-140 MB over 1,800 frames.
- **`UIElement.Clip`** is honoured by the rasteriser (the clip geometry's bounds, in the element's
  coordinates, narrowing it and its descendants; exact for RectangleGeometry). **`SkewTransform`**
  exists and is drawn by the skew path. **Storyboard paths with indexers** (`(TransformGroup.Children)[3]`,
  `Children[3]`) resolve. **`RepeatBehavior` parses from XAML** (`Forever`, `3x`, a TimeSpan): every
  XAML storyboard used to run once. Together: Solitaire's loading animation (a sprite sheet slid
  behind an 85x85 clipped Canvas by a TransformGroup's fourth child) loops its frames.
- **RichTextBox draws inline elements**: each paragraph is an `InlineFlowPanel` (WPR-internal) of
  words, spaces, breaks and InlineUIContainer elements, wrapped and aligned, runs keeping their colour,
  size and weight. Minesweeper's "A [1] means exactly one mine…" shows its tile pictures.

**Open:** ApplicationBar and the panorama backdrop are WPR's. Not run on Android, D2 or D4.

### WPR Hub: leaderboards and automatic crash reports (2026-09-27)

The online backend is **WPR Hub** (Laravel, `C:\Users\BenSl\PhpstormProjects\WPR-Hub`). Same
three-part shape as vibration and launchers:

* **Contract**: `Src/Engine/WPR.Engine.Online`, which holds `ILeaderboardService`, `ICrashReporter`,
  `CrashReport`, `IGamePackageSource` and `GamePackageRecord`. It speaks strings, longs and
  `Exception` only, because `WPR.Framework.Xna` and `WPR.Engine.GameLoop` consume it.
* **Registry**: `OnlineBackend.Leaderboards` / `.Crashes`, filled by `caps.Online(...)`.
  `OnlineBackend.FlushAsync()` sends everything queued.
* **Implementation**: `Src/Modules/Online/WPR.Online.Hub`. `Client/` is the hub repo's own C#
  client (`client/WPR.Hub`), brought in so it is actually compiled. It differs from the hub's copy
  in two ways: there is no default base URI, and the queues are one file per item. Both heads build
  it through `WPR.Shell.HubSetup.TryCreate`.

**Settings** (`Configuration`, all read live):

* `HubUrl` defaults to `Configuration.DefaultHubUrl`, which is **https://wpr.it-stacks.com/**. An
  empty value, or one equal to the default, is stored as null, so the default can move.
* `HubAccessToken` / `HubUsername`: null means signed out.
* `ServerLogging` defaults to **on** (product decision, 2026-09-27). It is persisted as the
  nullable `ServerLoggingEnabled`, **not** `ServerLogging`, because the first build wrote
  `"ServerLogging": false` as its default.

Both settings pages expose the switch. **The hub address is not in either settings page (2026-09-29)**: players always use the official hub. `HubUrl` in config.json still overrides it, which is how the local test hub is reached.

**Every install offers its package, not only crashes.** After writing `wpr-package.json` the
installer queues `CrashReport.ForInstall(...)`, which is sent as a hub **`manual`** report with no
stack, and each head flushes after a successful install. A diagnostics receipt is the hub's only
anonymous route to an upload offer, so this works against the deployed hub unchanged. The cost is
one manual report row per install in the admin area. A dedicated anonymous "offer" endpoint on the
hub would be cleaner.

**Crashes are queued on disk, never sent from where they happen.** `Report()` only writes a file
under `<DataStore>/Online/reports/`. The launcher flushes at startup and after every game run.
This one rule covers both a dying process and Android's `:game`, which `OnDestroy` kills. There
are three capture sites:

| site | catches |
| --- | --- |
| exception filter on `ApplicationLaunch.Start` | anything escaping a game run, XNA and mixed-mode. The filter returns false, so the stack and teardown are untouched. The one remaining unsupported-runtime refusal is sent as a `refused` report instead (2026-10-14) |
| `InstallCrashReportHook` (AppDomain unhandled, **Release too**) | a worker thread killing the process. `InstallCrashHooks` is `#if DEBUG` and was not enough |
| `GameLauncher.HandleGameResult` (Android launcher) | a `:game` death with no managed exception, reported as `NativeCrash`. It is dropped if a managed report for that title landed within 2 minutes |

The desktop Silverlight UI host reports through its own filter in `MainWindowDesktop`.

**Game uploads**: the installer hashes the package (`GamePackageRecord.Measure`) and writes
`wpr-package.json` into the install folder. That record is the only way to know the original
build, because the folder holds patched DLLs. When a receipt says `game_file.wanted`, the reporter
gets the package back through the head's `IGamePackageSource` and **re-checks the SHA before
sending**. The desktop uses the library file path. Android uses a persisted `ACTION_OPEN_DOCUMENT`
grant, copied into the cache for the duration of the upload. **Games installed before this have no
record**: they report without a hash, and the hub can never ask for their package until they are
reinstalled.

**Leaderboards**: `LeaderboardEntry.Rating`'s setter commits on the next game-thread pump, so
columns set after `Rating` go with it, and several assignments in one frame make one write. Board
key is `<Key>:<GameMode>`. Writes queue while signed out and are sent the first time a token
appears. Reads (`LeaderboardReader`, all three views plus paging) return the old empty board unless
signed in, and a failed read is an empty board, never a fault in the game's callback.

**Reading a board creates it on the hub.** `LeaderboardReader` calls
`ILeaderboardService.EnsureBoard` before every read, signed in or not. That hits the hub's
anonymous `POST /api/leaderboards/{titleId}/boards` (added 2026-09-27). It is per-IP rate limited
(30 a minute, 500 a day), subject to the same 25-undefined-boards cap as a first score, and returns
nothing about players. Registration happens once per board per process, and a failure clears it so
the next read retries. The signed-in `GET …/{key}` also creates a missing board rather than
returning 404. The same hub change made `guessSort` treat `BestScore*` as highest-wins and
`BestTime*` as lowest-wins, ahead of its `time` regex.

**Sign-in, gamerpics and manual reports (2026-09-28).**

* **Sign-in** is the hub's device-code flow. `HubAccountService` (`HubOnline.Account`) does it,
  and `SignInWindow` (desktop) / `SignInActivity` (Android) show it: the link key, a button to the
  `/link` page, and a live expiry countdown. After approval it saves the token and username, turns
  the account's server logging on to match WPR's switch, pulls the gamerpic, and flushes queued
  scores. **Without the server-logging sync, a newly signed-in player's uploads would be refused**,
  because signed in, the hub goes by the account setting.
* **Desktop sidebar order (2026-10-02)**: PLAY, TROPHIES, HUB, MESSAGES, CONTROLS, SETTINGS, ABOUT.
  `MainViewNavigator.PageFactories` lists the pages in the same order, and the tab index is what
  selects one, so the XAML and that list change together. The desktop shows only the white hub
  logo (`Images/white_wpr_hub.png`).
* **Trophy (achievements) page names (2026-10-02)**: both heads name a game from the bundled
  catalogue (`HardcodedAchievementCatalogue.GameName`) first, then the install record. The desktop
  then asks WPR Hub (`HubSocialService.GetTitleNameAsync`, anonymous) for any still showing a
  product id, through `WPR.Shell.HubTitleNames`, which caches answers in
  `Online/title-names.json`. Android does not ask the hub yet.
* **The Android settings page has no account section (2026-10-02).** Sign in, sign out and the
  gamerpic picker are on the Start screen's hub tile and `Native/HubActivity` (which gained the
  sign-out button). The desktop settings page still has its Online account row.
* **Gamerpics**: `GamerpicWindow` / `GamerpicsActivity` over `GET /api/gamerpics` and
  `PUT /api/me/gamerpic`. The chosen picture is saved as `<DataStore>/Online/gamerpic.png` and made
  the in-game `Configuration.GamerPicturePath` by `HubSetup.GamerpicChanged`, always (there is no
  local picture to protect: the desktop's file picker and default-avatar strip were removed on
  2026-10-02). Sign-out clears it, so games fall back to the bundled default.
  `GetImageAsync` fetches only from the configured hub's own host.
* **Reports are "play with logging" runs (2026-09-29)**, which replaced the settings pages' "send a
  report" box (its fixed 20-minute window rarely held the moment that mattered). Start one from a
  game's long-press list on Android or the **Play with logging** button in the desktop game pane.
  The run sets `WprHostEnvironment.DiagnosticRun`, so `ApplicationLaunch` attaches the per-game
  trace log and the first-chance logger that Release builds otherwise leave out (`_verboseLaunch`;
  always on in Debug). A **stop & send report** button sits over the game, tapped twice to fire:
  on Android a view `GameActivity` adds over the SDL surface, on the desktop a topmost
  `Views/LoggingRunWindow`. Whether the player stops it or the game exits or crashes, the
  **launcher** sends the run's `SessionLog` from its start time (plus the app's logcat on Android)
  through `HubCrashReporter.SendManualAsync` and shows the report code. It sends with server
  logging off, because asking for the run is the consent. **The run is a file**
  (`Online/diagnostic-run.txt`, `WPR.Shell.DiagnosticRun`), because on Android the stop button kills
  the `:game` process and the launcher may have been recreated meanwhile. A normal launch clears any
  leftover record so it cannot turn into a report. On the desktop, a game that does not close
  within 8 s of the stop is reported anyway, while it is still running.
* **`SessionLog` is new, and it is what makes any of this possible in Release.** `Log` wrote only
  to stdout, which the desktop WinExe discards, and `wpr_game_debug.log` is `#if DEBUG`. Every
  `Log.*` and `Trace` line now goes to `<DataStore>/Logs/wpr-<process>.log` (`launcher`, `game`),
  one file per process because Android runs two, capped at 4 MB by halving. Automatic crash reports
  fall back to its last 10 minutes when there is no per-game log.
* **Logo** (2026-10-02): two files in `Images/`. `wprhub.png` is the full-colour logo, for anything
  on black (the hub pages and sign-in, both heads; shipped as `Assets/wpr-hub-logo.png` /
  asset `hub/wpr-hub-logo.png`). `white_wpr_hub.png` is the white version, for the
  accent-coloured Android Start tile (asset `hub/wpr-hub-logo-white.png`, `HubLogo.LoadWhite`).
  The tile stays the accent like every other primary tile; a branded near-black tile was tried and
  rejected. On Android the colour logo's near-black square is keyed to transparent at load
  (`HubLogo.KeyOutBackground`), because it is not quite even and showed a faint box. Both are
  optional: without them the pages show a "WPR HUB" text header and the tile its glyph.

**Leaderboard reads fill the rating column, and carry gamerpics (2026-10-03).** Almost every WP7
title posts with `entry.Rating = score` and reads back with `Columns.GetValueInt32("BestScore")`
(Mirror's Edge, Dream Track Nation: `"BestTime"`); a scan of 34 installed leaderboard games found
only those two names. Xbox LIVE's standard boards filled that column from the rating, the hub
stores only what a game set (`extra`, usually null), so every row read 0.
`LeaderboardEntry.FromRow` now adds `BestScore`/`BestTime` from the rating by the identity's key
prefix (`RatingColumn`), unless the game wrote that column itself. `GetValueInt32` on a long
column clamps rather than throwing an `OverflowException` into the game's callback.

`GetGamerPicture` re-encodes every picture to **64x64** (`AtGamerPictureSize`: decode at native
size, scale in `WritePNGStream`), because WP7 titles draw it at its own size: the hub's 256px
gamerpic covered a quarter of Fruit Ninja's home screen. **Never use `ReadImageStream`'s
`forceW`/`forceH` on Android**: FNA3D_Image_Load's resize path `SDL_free`s stb's SIMD-aligned buffer,
and scudo aborts the process (`Scudo ERROR: corrupted chunk header`, SIGABRT on a worker thread,
reported only as "the game process exited unexpectedly"). The desktop allocator tolerates it, so a
Windows test passes. `Texture2D.FromStream(stream, w, h, zoom)` reaches the same path. Done at read time
rather than download time so installs that already cached the 256 file are fixed too. Leaderboard
rows now carry their player's hub gamerpic (`LeaderboardRow.Gamerpic`, fetched as the hub's `-64.png`
variant, hub host only, 5 s each, cached by URL, before the page is returned since games ask for it
synchronously from draw code); `LeaderboardGamer` overrides `Gamer.IsOtherPlayer`/`PictureBytes`, so
another player's profile never falls through to the local picture (bundled default when they have
none). **The friends overload** (`BeginRead(id, gamers, pivot, …)`, Angry Birds' only leaderboard)
reads the hub's friends view, i.e. WPR Hub friends only, as Xbox LIVE did. A player with no hub
friends sees only themselves there; a fall-back to the whole board was tried and rejected
(2026-10-03). Angry Birds also draws the player twice by design: a pinned own-score bar, then the
list. Verified with the real `LeaderboardReader.Read(identity, signedInGamer, 25)` against the live
hub: all players returned, `BestScore` equals the rating, distinct pictures per row.

**Achievements, playtime and presence (2026-09-28).**

* **An unlock is always recorded locally first.** "Awaiting upload" means `IsEarned = 1 AND
  EarnedOnline = 0` in `achievements.db`. `EarnedOnline` is XNA's own column, already in every
  shipped database, so no schema change was needed. `SignedInGamer` now writes it **false** (it
  used to write true unconditionally), and calls `OnlineBackend.Progress.Unlocked()`, which uploads
  straight away when signed in. `HubProgressSync` is the only thing that sets it true, and only
  after the hub has answered. So offline, signed out and "the send failed" are one state with one
  way out: the next flush.
* **A one-time reset** (`WprOnlineMeta` marker `achievements-earned-online-reset-v1`) turns every
  unlock earned before this existed back into awaiting upload, because none of them was ever sent.
  Measured on the S24: `10 earlier unlock(s) marked awaiting upload` → `achievements uploaded: 10
  (hub: 10 new)`.
* **`EfOnlineLocalStore` (`WPR.Database/Online/`) uses raw SQLite on its own connections, not
  `AchievementContext`.** That context is a tracked singleton the game thread uses during an award,
  and a DbContext is not thread-safe. Writing only `EarnedOnline` is safe against the tracked copy
  because EF writes only the properties it saw change.
* **Playtime**: `PlaytimeTracker` (engine) runs one session per game run, started and ended in
  `ApplicationLaunch.Start`. It pauses on the game's
  `Deactivated` and resumes on `Activated`, and checkpoints every minute to a new `PlaySessions`
  table in `achievements.db` (`CREATE TABLE IF NOT EXISTS`, no migration). Each unlock is sent with
  the playtime before it, from those sessions. Sessions under 5 s are dropped as failed launches.
* **Presence**: `HubPresence` beats `PUT /api/me/presence` about once a minute while signed in,
  saying "playing X" while `PlaytimeTracker` has a game. On Android the launcher and `:game` each
  run one, and **`GameLauncher` suspends the launcher's while a game runs**, or the hub would see
  "idle" and "playing X" alternate. The desktop sends "offline" on window close.
* **Hub side**: `online_sessions` (migration `2026_09_28_000100`) plus `OnlineSessionService`
  turn heartbeats into sessions: per device token, closed after `social.online_seconds` of silence
  or on an "offline" beat, with seconds spent in a game. They are listed on the admin player page,
  included in `/api/me/export`, and pruned after a year. **This needs the hub deployed and
  migrated**; until then presence still works, it just is not logged.
* **The WPR Hub page (2026-09-28)** is a Start tile on Android (`tileHub` → `Native/HubActivity`)
  and a `HUB` tab on the desktop (`Pages/HubPage`; see the tab order note below). Profile (gamerpic, name, games · playtime · achievements from `/api/me/games`), incoming
  friend requests, friends with presence, add a friend by username, outgoing requests, and games
  with local tile art. Calls are `HubOnline.Social` (`HubSocialService` over `Client/Social.cs`);
  wording is shared through `WPR.Shell.HubText`. The Android tile's "N friends online" costs no
  request: it reads `HubPresence.LastBeat`. Tapping an installed game on the Android page launches
  it through `GameLauncher`. Messages are not built yet, though the hub has them.
  `scratchpad/hubprobe social` checks the calls end to end (11 checks).
  The hub tile is the **top** Start tile, with two faces: signed out, a basic "wpr hub / sign in or
  sign up" tile that opens `SignInActivity`; signed in, a **gamer card** (gamertag; gamerscore
  summed from this device's earned achievements; friends online from the last beat; down the left,
  with the white logo bottom right). **No gamerpic on the card, by choice (2026-10-02)**; the
  picture is still saved and still the in-game gamer picture. Nothing on the card needs a request.
  Games is a square tile now.
* **Messages (2026-09-28)**: a `messages` Start tile (row 2, beside games; the number is unread
  messages from `LastBeat`) → `Native/MessagesActivity` (message a player by username, send a friend
  request, conversations, friends not messaged yet) → `Native/ConversationActivity` (bubbles, reply,
  polls `?after=<id>` every 5 s while in front, marks read, long-press to copy / delete for me /
  report and optionally block). Desktop: a `MESSAGES` tab (`Pages/MessagesPage`), two panes,
  same behaviour. **The hub only carries messages between friends**, so a non-friend username is
  offered a friend request, and a conversation whose `is_friend` is false is read-only. Calls are
  `Client/Messages.cs` via `HubOnline.Social`; `Native/HubViews` holds the Android hub screens'
  shared row and button builders. MainActivity nudges presence on resume so the tile counts catch
  up in seconds. `scratchpad/hubprobe messages` checks it end to end (14 checks).
* **Social + appear offline (2026-09-29, Android)**: the messages tile is now `social` →
  `Native/SocialActivity`, two WP pivots: **friends** (appear-offline switch, requests, friends, add,
  outgoing) and **messages** (the old messages page). It opens on messages when `LastBeat` has
  unread. A friend row (here and on the hub page, both via `Native/HubFriendViews`) opens
  `Native/FriendProfileActivity` over the hub's new `GET /api/friends/{username}`: presence, totals,
  and the latest 30 unlocks/play sessions. (Named `...ProfileActivity` because the client record
  `FriendActivity` is an activity-feed item.)
  **Appear offline** is `Configuration.HubAppearOffline`, read live through `HubSettings.AppearOffline`;
  `HubPresence` then beats as `invisible` (`PresenceStatuses.AppearOffline`), so messages and counts
  keep working. Hub side (migration `2026_09_29_000001`): an invisible beat opens its own
  `online_sessions` row with `appear_offline = 1` (a visible/invisible switch splits the session),
  and `users.visible_seen_at` moves only on visible beats, or on a sign-off that ends a visible run.
  Friends' "last seen" is `visible_seen_at`, so appear-offline time plus real offline time read as
  one offline stretch. The friend feed drops any unlock or play session inside an appear-offline
  session (end padded by `online_seconds`). **Totals (games, achievements, G) still count
  everything**, so they can rise while a friend looks offline — accepted, not an oversight.
* **Sign in and sign up are one screen** on both heads: "Continue with GitHub / Microsoft / Google",
  then the key and the wait. The hub lists its enabled providers in the device-code response
  (`providers`), and `/link?code=…&provider=x` skips straight to that provider; the player still
  returns to the link page to check the key and approve. A hub without the list gets the three
  defaults (`SignIn.Providers`) and ignores the hint. **A new account chooses its gamertag
  (2026-10-02)**: the hub's `/link` page shows a "choose your gamertag" form (suggested from the
  provider nickname) before Approve, and `POST /link` refuses to approve until
  `users.username_chosen_at` is set (migration `2026_10_02_000001`; existing accounts are
  backfilled and never asked; `PATCH /api/me` also sets it). Rules are `User::usernameRules`.
  **What games see is derived, never set (2026-10-02)**: `Configuration.EffectiveGamerTag` is the
  hub username when signed in and `GuestGamerTag` otherwise; `EffectiveGamerPicturePath` is the hub
  gamerpic when signed in and null (the bundled default) otherwise. `Gamer` and `GamerProfile`
  read only these. The old `GamerTag` setting is no longer read, and neither settings page has a
  gamertag or picture field. `GuestGamerTag` is `player` + six digits, made and saved on the
  first `Configuration` load. It never leaves the device: signed-out scores still wait and go up
  under the account at sign-in. The hub refuses `player` + digits for any account
  (`User::isGuestStyleUsername`, in both `usernameRules` and `isValidUsername`), and its own
  fallback name is now `gamer####`; older `player####` accounts keep their names, and since those
  are four digits they still cannot equal a six-digit guest name.
  **Google needs `GOOGLE_CLIENT_ID` /
  `GOOGLE_CLIENT_SECRET` on the server**; unconfigured providers are not offered (unless none are
  configured at all, the local-dev case).
* **"How did it run?" ratings (2026-09-29)**: after a game, now and again, the launcher asks for
  one of the hub's six compatibility ratings (`perfect` … `nothing`) and posts it to
  `POST /api/compatibility` (the hub's own feature, signed-in only). The rules are all in
  `WPR.Shell.GameRatingPrompt`, so both heads ask on the same terms: at most once per cooldown
  (`Configuration.RatingPromptCooldownMinutes`, default 60, 0 = never, -1 = after every game;
  "Ask how a game ran" on both settings pages, choices in `GameRatingPrompt.CooldownChoices`). The
  question offers "ask me later" (also what closing it does: the game is asked about again the next
  time it is finished, once the cooldown allows) and "don't ask about this game" (never again on
  this WPR version). The earlier "never the same game twice in a row" rule was dropped, since
  postponing exists to ask about the same game again. Otherwise: never again for a game rated on
  this WPR version, only after a run that ended normally and lasted a minute, and only when signed
  in. A question shown starts the cooldown whatever the answer, and a rating that fails to send is not remembered as answered.
  State is `Online/rating-prompts.json`. Android asks in `GameLauncher.MaybeAskRating` (not over
  the shortcut trampoline, which finishes at once); the desktop uses `Views/RateGameWindow`.
  `scratchpad/hubprobe rating` checks the rules and the send (12 checks).
* `caps.Online(...)` takes one `OnlineServices` bundle (leaderboards, crashes, progress, presence,
  local store), built by `HubOnline.Services`.

**Android Release needs `JsonSerializerIsReflectionEnabledByDefault=true`** (set in the head
csproj), because the client serialises records and anonymous types by reflection. Both assemblies
are trimmer roots too.

**Hub-side sort bug (fixed in the hub repo 2026-09-27, see above)**: `app/Models/Leaderboard.php` guessed sort order with
`/time|…|lap/i`, which matches **`BestScoreLifeTime`**, XNA's most common key. Undefined boards
with that key rank lower-is-better until the definitions repo defines them.

**Verifying**: run the hub from a scratch copy (`composer install`, `QUEUE_CONNECTION=sync`,
sqlite, `php -S 127.0.0.1:8765 -t public public/index.php`) and mint a token with
`php artisan tinker` (`$u->createToken('x', config('wpr.device.token_abilities'))`). The
session's `scratchpad/hubprobe` harness ran 22 checks end to end (queue, send, upload + hub
verification, dedupe, consent off, real `LeaderboardWriter`/`Reader`), all passing. The local PHP
has no sodium extension, which only breaks `/api/services/*`.

Unrelated, and present before this change: the **Android Debug** build fails
`MSB4096 … TrimmerRootAssembly … "RootMode"` in the SDK's `_FixRootAssembly`. Release (no
trimming) builds fine.

No `ApplicationPatcher.Version` bump. Nothing needs a repatch; only a reinstall writes the package
record.

### Installs fill in the hub's game details: name, publisher, icon (2026-10-23)

Title pages on WPR Hub used to need an admin to type every name and upload every icon. Now each
install offers what its package says about the game, and the hub keeps only what it lacks.

* **Contract**: `WPR.Engine.Online.ITitleDetailsSync` + the `TitleDetails` record (title id, name,
  publisher, developer, platform, the icon's path), slot `OnlineBackend.TitleDetails`.
  `OnlineBackend.FlushAsync` flushes it with the rest.
* **Implementation**: `WPR.Online.Hub.HubTitleDetails`, over `Client/Titles.cs`.
* **Callers**: `ApplicationInstaller.Install` and `RepatchAsync` call `OfferTitleDetails`, which only
  queues. `ApplicationInstaller.DescribeForHub` builds the record: `Application.Name/Publisher/Author`,
  the platform from `WMAppManifest.xml`'s `AppPlatformVersion` (7.x, 8.0, 8.1), and the installed
  `IconPath` file, which is read when the queue is sent, not when it is queued.

How it behaves:

- **Queued and flushed, like the install receipt beside it.** `Online/title-details/` holds one file
  per title (prefixed with the id, so a second offer before a flush does nothing). The flush asks
  `GET /api/titles/{id}/details` which fields the hub still wants, then `POST`s only those (the icon
  as base64 of the package's own PNG/JPEG, at most 256 KB). An install never waits on this or fails over it.
- **Asked once per hub.** Whatever the answer (kept, nothing wanted, 422/404 refused), the title
  goes into `Online/title-details-sent.txt` as `<host>|<ID>`, so a second install, a repatch or a
  later launch sends nothing, not even the GET. A network error or a 429 leaves it queued and ends the
  flush. The host is in the key so a local test hub and the real one are told separately.
- **Backfill.** The first flush of each process lists every installed game
  (`ApplicationInstaller.InstalledTitlesForHub`, on its own `ApplicationContext` because it runs
  on a flush thread) through `HubSettings.InstalledTitles`, and queues the ones not sent. That is how
  games installed before this existed, or while server logging was off, reach the hub.
- **Gated on server logging**, the install receipt's consent: it tells the hub this address
  installed the game. With it off, nothing is queued, backfilled or sent.
- **At most 10 titles per flush**, because the hub allows 30 requests a minute and 300 a day per
  IP, and a title costs one or two requests.
- An unresolved resource name (`@AppResLib.dll,-100`) is not sent as a name, and an icon that is not
  a PNG or JPEG is left out.

**Hub side** (branch `title-icon-uploads` in the hub repo): `Api/TitleDetailsController`, both
routes anonymous behind the `title-details` limiter (30/min, 300/day per IP; POST `body.limit:400`).
**It only ever fills an empty field.** An existing icon is never replaced, whether an admin or an
earlier install set it, and a name is replaced only when it is the "Unknown game" placeholder (a
title created by a score or an unlock before anyone named it). A title the hub has never seen is
created. The icon must be a PNG or JPEG, 32 to 1024 px, at most 256 KB. It is decoded and re-encoded
as a 256 px square PNG, the same as admin uploads (`GameImageService`), and marked
`icon_source = wpr-install`, which the admin game page shows as "icon from a player's install". An
admin changing the icon clears that mark. Title pages (`/compatibility/{id}`) already showed
`icon_url`.

Verified 2026-10-23 against a scratch hub, with the real installer in a console probe: Flowerz
installed and the hub got its name, publisher, developer, platform and the 62 px tile, served at
256 px on its compatibility page. Reinstalling Flowerz made no details requests. AlphaJax, with an
admin name and icon set first, got its empty publisher, developer and platform filled, while the
name and icon stayed and no image was stored. With the sent log deleted, a fresh process
backfilled both: Flowerz (whose hub row had been deleted) was sent again, and AlphaJax took one GET.
Tests: `HubTitleDetailsTests` (11) and the hub's `TitleDetailsTest` (9). Not run on Android: the
code is shared and the head only passes the backfill delegate.

No `ApplicationPatcher.Version` bump and no reinstall: existing installs are backfilled on the next
launch.

### Every game is attempted; what it carries on past reaches WPR Hub (2026-10-14)

Two halves, one policy (the "run unknown games until they fail" rule): WPR refuses a game up front
only when there is genuinely nothing to run, and everything a run meets, crash or not, is told to
the hub. All of it goes through the existing `ICrashReporter` disk queue, so it is **gated on
server logging exactly like a crash report** (off: `Report` is a no-op, nothing is queued or sent)
and sent by the launcher's usual `OnlineBackend.FlushAsync()` after the run.

**Refusals removed** (each now attempts the game instead):

| was refused | now |
| --- | --- |
| unknown `RuntimeType` (`NotSupportedAppType`) | guessed: a `.exe` default task is native, anything else goes to `SilverlightGame` (which hosts mixed-mode XNA too) |
| native package whose default task names no image in the package (`ModernNativeUnsupported`) | the package's one `.exe` if it has exactly one; refused (and reported) only when there is none |
| patcher threw at install (`PatchFailed`) | installed anyway with `PatchedVersion = 0` (Android repatches next launch, desktop offers Repatch); `install-problem` report |
| audio transcode failed at install (`ConvertFailed`) | installed anyway; `install-problem` report. Supersedes the "missing transcoder fails the install" note below |
| Android repatch-before-launch threw: error dialog, no launch | launches what is on disk, `PatchedVersion` left behind so the next launch retries; `install-problem` report |
| Unity port with no build for this platform: error (desktop threw, Android dialog) | falls through to the original package in-process. Android still asks the player to install a bundled port APK that is sitting right there |
| WP8 managed title registering a native helper library | `NativeXamlComponent` treated any native ARM `InProcessServer` as a Direct3D/XAML game, so the WP8 host got a library with no game in it and stopped at frame 0 ("no page description"). Now skipped: `SharpDX*`, `Lumia.*`, `Nokia.Graphics*`, `Microsoft.Xbox.dll`, and a non-drawing component unless the manifest names the `Direct3DXaml` template. **What In The World?** (Lumia.Imaging) and **Microsoft Minesweeper** 1.1.41016 (SharpDX.WP8) now run on `SilverlightGame` and report what they miss. Installed copies keep their old type until reinstalled |
| no / unreadable / incomplete `WMAppManifest.xml` or `AppManifest.xaml` (2026-10-16). Desktop: the library scan dropped the package silently, the picker showed "invalid manifest". Android: the same dialog. Nothing was ever half-installed: the refusal came before any extract | completed or synthesised from the contents by `WPR.PackageManifests` (`Src/Core/WPR.Loader/PackageManifests.cs`) - see below |

**Manifests are synthesised when missing or broken.** `PackageManifests.Load` is the one reader
both `ReadPreview` and `Install` use. A whole package takes a fast path (no assembly is opened);
otherwise it inspects the package with Cecil: a type reaching `Microsoft.Xna.Framework.Game` is XNA,
one reaching `System.Windows.Application` is Silverlight (start page = the XAML a
`PhoneApplicationPage`'s `InitializeComponent` loads: MainPage, then a Splash/Loading page, then
Main/Start), an ARM PE `.exe` (0x01C4, no CLR header) is native; an AppManifest.xaml that names an
entry point decides the managed side, and base classes are followed across the package's
assemblies to the most-derived type in the top (unreferenced) assembly. Identity, in order: the
manifest's own attributes, WP8.1 `AppxManifest.xml` (`PhoneProductId`, `DisplayName`, `Executable`),
the entry assembly (`[Guid]`, `AssemblyTitle`/`Product`, file/assembly version, company), the package
file name, and for the ProductId a GUID from the SHA-256 of the entry image, so the same package
always gets the same id (the scan compares ids). A missing icon is guessed from PNG names
(`*icon*` before `*tile*`). The completed manifests are written into the install folder (the package's
broken one kept as `.original`), because `SilverlightAppHost` (start page) and the WP8 runtime
(ProductID, `ActivatableClasses`) read `WMAppManifest.xml` from there; a synthesised Silverlight
manifest lists the package's native ARM DLLs as `InProcessServer`s so `NativeXamlComponent` can still
spot a Direct3D/XAML game. Every such install logs `Package manifests incomplete (<keys>)` with where
each value came from, and queues an `install-problem` report `WPR.InstallProblem.ManifestSynthesised`
whose message is the stable key list (`wmappmanifest-missing|-invalid|-incomplete`,
`appmanifest-missing|-invalid`) and whose `extra.synthesis_detail` has the values.
Verified (desktop D3D11, scratch data root, stripped copies; no manifest-less package exists in the
library): Angry Birds with neither manifest -> XNA, menu; with a truncated WMAppManifest -> XNA;
Flowerz with neither -> Silverlight, starts at `Pages/Loading.xaml` like the real manifest, menu;
Angry Birds Rio without WMAppManifest -> native, title screen; a ZIP holding only a text file -> still
refused (`MissingManifestFiles`); Sonic Dash still `NotDecrypted`. Not run on Android. Limits: the
ProductId is not the store's, so catalogue achievements and old saves do not attach;
`.appxbundle` is not unpacked; `LibraryScanner` still lists `*.xap` only.

**Real refusals that stay, and are now reported** (`refused` source): a package with no usable
manifest and nothing runnable in it (`MissingManifestFiles`; `InvalidManifestFiles` from a head
whose preview could not read the package at all, e.g. not a ZIP), a DRM-wrapped Marketplace download (now detected by its `PRE\x07` magic and
reported as `NotDecrypted` rather than "unexpected error" - BBB: App-ocalypse and Sonic Dash), a
native package with nothing executable, an installer that threw, and `ApplicationLaunch`'s
"no host for this runtime type". `ApplicationInstaller.Install` reports its own refusals;
`ApplicationInstaller.ReportRefusedPackage` is what both heads call when `ReadPreview` cannot even
read the manifest. A refusal carries the package's SHA-256 (`CrashReport.Package`, used when there
is no install folder), so the hub can ask for a package it has never seen. `LibraryScanner` still
silently skips unreadable packages: a scan is not an attempt to play, and reporting it would send a
library's worth of reports. Still refused at run time, already reported as a guest failure: a
Direct3D/XAML component with no `XamlShells` description.

**The problems a run carries on past** (`compat` source), from
`WPR.Engine.Online.CompatibilityProblems`, begun and ended by `ApplicationLaunch.Start`:

- **A first-chance observer, live in Release.** Every exception in the process pays a null check
  (no game running) and a type test; only an allow-list is looked at further: `TypeLoadException`
  (missing type), `MissingMemberException` (method/field), `FileNotFoundException`/`FileLoadException`
  **only for an assembly load** (a save probe is not one), `EntryPointNotFound`/`DllNotFound`,
  `BadImageFormat`/`InvalidProgram`, `NotImplemented`/`PlatformNotSupported`, and the loader
  exceptions inside a `ReflectionTypeLoadException`. The runtime's own `.resources` and
  `.XmlSerializers` probes are excluded; dead online services are never on the list. This is not
  the verbose `[wpr-fce]` logger, which stays Debug / logging-run only.
- **Frame exceptions**: `Game.Update`/`Draw` and `GameTimer` catches call
  `NoteFrameException` (keyed by type + throwing method, so 7,874 identical NREs are one line with a
  count). A Silverlight quit (`GameUnhandledException` declined) is not a problem.
- **`[wpr-uirender]`**: unsupported rasteriser features, unresolvable images, rasteriser throws.
- **Deduplicated and bounded**: 40 distinct problems (then a dropped count), every string truncated,
  only a new key pays for a stack walk (16 frames, taken on the throwing thread).

**One hub crash report per distinct problem (2026-10-14, supersedes "one `manual` report per run").**
The first version sent each run's list as ONE kind-`manual` report titled
`WPR.CompatibilityProblems`, which the hub never grouped, so "RichTextBox is missing" in ten games
was ten unrelated rows. Now `CompatibilityProblems.BuildProblemReports` makes one kind-**`crash`**
report per problem, shaped for the hub's `CrashFingerprinter` (exception type + top five `at` frames):

```
exception_type    System.TypeLoadException          (WPR.Compatibility.<category> for a problem WPR noticed itself)
exception_message missing-type: System.Windows.Controls.RichTextBox [System.Windows]  (+ the full message)
stack_trace       at WPR.Compatibility.missing-type: System.Windows.Controls.RichTextBox [System.Windows]
                  --- first seen 0.9 s into the run, x24 (not part of the fingerprint) ---
                      from System.Reflection.RuntimeAssembly.GetExportedTypes
                      from WPR.SilverlightCompability.XamlTypeConverter.ResolveTypeByName ...
extra             source=compat problem_category problem_key problem_count first_seen_s outcome run_seconds run_problems
```

- **Exactly one `at` line, the synthetic frame.** Where the problem was first seen is written as
  `from` lines, because the fingerprinter reads every `at` line as a frame: kept as frames they
  would split one problem into a group per game. The same rule applies to the list attached to a
  real crash (`Describe`): a native crash has no frames of its own, so the problems' frames became
  its fingerprint (measured: a `NativeCrash` group whose top frame was `GetExportedTypes`).
- **Bounded and deduplicated, in the reporter** (`ICrashReporter.ReportCompatibility`;
  `HubCrashReporter` implements it, the default just caps): at most **10 per run**
  (`MaxReportsPerRun`), the last carrying `more_problems` + `mNN` lines for the rest
  (`SelectForSending`); and a (title, problem, WPR version) already sent or still queued is not
  sent again (`Online/compat-sent.txt`, 2,000 SHA-256 prefixes, `SentId`). So the second run of a
  game sends nothing it sent before; a new WPR version or another title sends it once more. They
  queue in `Online/compat-reports/` (capacity 50), separate from the crash queue (capacity 20)
  so ten problems can never push out a crash, and flush after it.
- **A crash still sends the crash first, with the list attached, AND the per-problem reports**,
  minus the one that IS the crash (`KeyOf(exception)`, unwrapped like `CrashReport.FromException`).
  Choice: each problem is its own group and the crash is another, so nothing is counted twice, and
  a title that crashes still counts towards "RichTextBox is missing". Queued at crash time
  (`QueueProblemsBeside`), not at the end of the run, because an unhandled exception on a worker
  thread kills the process before the run's finally blocks.
- **Refusals and install problems are kind `crash` too** (`refused` / `install-problem` sources),
  so they group by reason on the crash page. Only the install receipt stays `manual`
  (`CrashReport.Sources.SentAsManual`). `IsNotACrash` still names all four.

**Hub side** (branch `compat-reports-as-crashes` in the hub repo): `DiagnosticReport::NOT_CRASH_SOURCES`
(`compat`, `refused`, `install-problem`) and the `realCrashes()` scope (`kind = crash` and
`extra->source` null or not in that list; a JSON path, which compiles on SQLite, MySQL and
Postgres). `CompatibilityService::forTitle` counts only `realCrashes()`, so the compatibility
page's crash evidence is unchanged by any of this. The admin crash list and crash page show the
source as a badge. **Needs that hub branch deployed**: against an old hub these reports are grouped
fine, but they count as crashes on the compatibility pages.

**A native crash no longer loses the list (2026-10-14).** The list lived only in the game
process, so a SIGSEGV on Android or `Fatal error. 0xC0000005` on the desktop (which takes all of
WPR down) sent nothing. Now each NEW problem is appended to `<DataStore>/Online/compat-run.txt`
(`CompatibilityProblems.RunFilePath`, set by `HubSetup.TryCreate`; null means nothing is
persisted). Line-based: a `name\tvalue` header (product, title, folder, graphics, started, pid),
then one `p\t...\t.` line per problem, escaped; a line without its closing `.` or a file without a
product is ignored, so a torn write costs one line. Repeats are not written (a persisted count is 1).

- **Who clears it**: the run itself once its reports are queued (`DiscardRunFile` in
  `ReportCompatibilityProblems`' finally, and after a crash report in `QueueProblemsBeside`).
- **Who reads it back** (`TakeRunFile` moves nothing: it reads, deletes, returns):
  - **Android `GameLauncher.HandleGameResult`**, `force: true` (the `:game` process is gone): a
    native death sends `NativeCrash` with the problems attached plus the per-problem reports
    (`CompatibilityProblems.ReportNativeCrash`); a leftover after a normal or managed end sends the
    per-problem reports only. The 2-minute "covered by a managed report" check still applies to the
    `NativeCrash` report; the per-problem ones are deduplicated by the sent log.
  - **Every `OnlineBackend.FlushAsync`** (`RecoverAbandonedRun`, `force: false`), which both
    launchers call at start-up: a leftover whose pid is dead becomes a `NativeCrash` report with
    `extra.recovered=next-start`. A pid that is alive (a `:game` still running; this process with a
    run active) keeps its file. On Unix the check is `/proc/<pid>`.

**Verified 2026-10-14** (desktop harness `gamerun` + `GAMERUN_HUB=1`, scratch data root, scratch
hub on :8767): Carcassonne → two crash groups, `System.TypeLoadException` /
`WPR.Compatibility.missing-type: System.Windows.Controls.RichTextBox [System.Windows]` and the same for
`BindingExpression`; a second run sent nothing (`0 of 2`); a run killed with `Environment.FailFast`
left `compat-run.txt`, and the next start's flush sent one `NativeCrash` (`recovered=next-start`, both
problems attached, no frames, so grouped by message) and deleted the file; the compatibility API and
`forTitle` counted only the native crashes. Cross-title grouping is the hub test
`test_compat_problems_group_across_games_and_carry_a_source_badge` (same shape, two titles, one group).
Unit tests: `CompatibilityProblemsTests` (persistence, torn writes, live-run protection, recovery,
per-problem shape, cap and summary, sent-id dedupe). **Not run on Android** (Release build only):
`HandleGameResult`'s read path is the same `ReportNativeCrash` the recovery test covers. Refusal and
install-problem reports as kind `crash` were not sent end to end.

Earlier verification of the collector itself, before the per-problem change: Microsoft Minesweeper
named missing WPR members (`ResourceDictionary.get_Keys`, `IAsyncAction.put_Completed`); What In The
World? crashed with an unhandled `MissingMethodException` carrying its three swallowed missing
members; Angry Birds, Flowerz, Big Buck Hunter Pro, Chickens Can't Fly (25 first-chance
online/serializer exceptions, all filtered) and Fast & Furious met none; BBB: App-ocalypse was
refused as `NotDecrypted`; with server logging off nothing reached the hub (`Report` and
`ReportCompatibility` are no-ops then; the run file is still written and read, but nothing is queued).

`HubCrashReporter`'s "native crash already covered by a managed report" check looks at managed
crash reports only; a compat or install report for the same title must not swallow a native crash.

No `ApplicationPatcher.Version` bump and no reinstall, except that a WP8 managed title installed as
native needs a reinstall to move to `SilverlightGame`.

### A game Android killed on purpose is not a native crash (2026-10-23)

Reported as "false NativeCrash reports for lots of games". The case studied, WPR-44SQYC8G (Angry
Birds Space, S24, `recovered=next-start`), was **not** a live game judged dead, though it looked
like one: the board reads that seemed to prove Space still running came from a *second* Space run
(pid 3409, 16:24:18). The phone's own records settle it:

```
am_kill : [0,1476,com.wpr.android:game,0,stop com.wpr.android due to installPackageLI]   # logcat -b events
pid=1476 process=com.wpr.android:game reason=16 (PACKAGE UPDATED)                        # dumpsys activity exit-info
```

**An APK install kills every WPR process, a running `:game` included**, and so does a force stop
(Settings, or `am force-stop`, which the GPU matrix recipe runs before each launch). The run file
is left behind, the next launcher's flush finds its pid gone (correctly), and files a NativeCrash.
On a phone that sessions install onto many times a day that is most of the NativeCrash group.
`/proc/<pid>` was never wrong here: `/proc` is `hidepid=invisible`, and the launcher and `:game`
share a uid.

- **`CompatibilityProblems.ProcessExitLookup`** asks the platform why a pid ended. Android answers
  from `ActivityManager.getHistoricalProcessExitReasons` (API 30+,
  `Online/AndroidProcessExits.cs`), wired in `AndroidPlatform`. PACKAGE_UPDATED,
  PACKAGE_STATE_CHANGE, USER_REQUESTED, USER_STOPPED and PERMISSION_CHANGE are not crashes: the
  run's per-problem reports are still queued, the NativeCrash is not (`[wpr-compat] … was ended by
  the system (package-updated)` in the launcher log). Every other reason, low memory included, is
  still a NativeCrash and now carries `extra.exit_reason` / `exit_description`. No record (desktop,
  API < 30, a failing lookup) keeps the old behaviour. `GameLauncher`'s no-run-file fallback asks
  about the newest `:game` death since the launch (pid 0).
- **A normal end is `reason=2 (SIGNALED) status=9`** (GameActivity's own `KillProcess`), after the
  file is already gone, so it never reaches this. A SIGNALED record with a run file left is a kill
  WPR did not ask for and stays a crash (`exit_reason=signaled-9`).
- **The run also holds a lock**, `compat-run.txt.lock` (`FileShare.None`, `flock` on Unix), from
  `Begin` until `DiscardRunFile`. It only ever adds "alive": a held lock keeps the file even from
  the launcher's `force: true` take (which could otherwise take a *different* game's live file),
  and from the End→Discard window. When free, the pid check decides exactly as before, so a file
  system whose `flock` does nothing cannot make a live run look dead. `HandleGameResult` waits up
  to 1.5 s for the exiting process's lock (`ExitedGameLockWait`).

**Diagnosing one of these**: `adb shell dumpsys activity exit-info com.wpr.android` lists the last
16 deaths with reason and description, and `adb logcat -d -b events | grep am_kill` gives the
kills. Both are read-only and safe on the user's phone. **Measure the blast radius on the hub**
with `extra->recovered = 'next-start'` on `exception_type = 'NativeCrash'`, split by day against
the dates builds were installed; after this, a NativeCrash without `exit_reason` comes from a
client before this change or a phone below Android 11.

**Seen in the same log, not changed here.** Every flush got `Too many requests`: the hub's
anonymous diagnostics limit is 10 a minute and 200 a day **per IP**
(`WPR_DIAGNOSTICS_PER_MINUTE`/`_PER_DAY`), while one run alone can queue a crash plus 10
per-problem reports, and a player trying games all evening passes 200. The client already stops at
the first 429 and keeps the queue in order; the limit is what is too tight (and carrier NAT puts
many players behind one IP). Raising it, or keying signed-in senders by account, is a hub change.
The AE Fruit Slash named-mutex failure in the same log is fixed on `claude/ae-fruit-slash-3ed415`
(c0e59e89).

Verified on `Pixel_Dev` (API 36, Release APK, hub pointed at a dead address), Cut the Rope running:
launcher killed and restarted → file and lock kept, nothing queued (toybox `flock` on the lock from
`adb shell` says HELD, on both `/storage/emulated` and `/data/media`, so the lock is real across
processes there); `adb install -r` mid-game → the line above, no report; `kill -SEGV` on `:game` →
NativeCrash queued with `exit_reason=crash-native`. Not run on the S24 (only its logs were read).
Tests: `CompatibilityProblemsTests` (exit reasons, lock held / let go during the wait / held past
it, lock-less files by pid). No `ApplicationPatcher.Version` bump and no reinstall.

### A failing game shows its error on the screen (2026-10-16)

Two failures used to leave the player with nothing: a **WP8 guest that stopped** (fault, wild
jump, trap, "no page description") called `Exit()`, so the window closed or sat black with no
reason; and a game whose **frames throw and draw nothing** ran on as a black screen for ever. Both
now show an **error screen**: game name, headline (exception type + message, or the guest's stop
reason), where (top managed frame without its path, or "WP8 native code, after N frame(s)"), a few
detail lines (the stack, or the WP8 post-mortem summary first), whether WPR Hub was told, and
"press Back to exit".

- **Seam**: `WPR.Xna.Rhi.ErrorScreen` (`WPR.Framework.Xna/Backend/ErrorScreen.cs`). `Request(report)`
  from any thread; `Game.Tick` takes it on the game thread and shows `ErrorScreen.Factory`'s
  overlay through `HostOverlay`, so the game stops running underneath (same slot as
  `LauncherMovieOverlay`). `ApplicationLaunch.StartCore` registers the factory per launch;
  `TeardownAudioState` resets it. No factory, or `WPR_ERROR_SCREEN=0`: a WP8 stop exits as before.
- **The screen**: `WPR.Engine.GameLoop/ErrorScreenOverlay.cs`, drawn on the CPU with the Silverlight
  `TextRasteriser` (system TrueType font, no content) into one backbuffer-sized texture, so it is the
  same on every driver. The top ~10% is left empty for Android's logging-run button (top right).
  **Only Back leaves** (phone button, Esc on the desktop); a tap does nothing. No font: a plain
  dark screen with the red bar.
- **The frame watch is conservative on purpose.** `NoteFrameException` (Game.Tick's Update/Draw
  catches, GameTimer via `GameUnhandledException.ReportFrameException`) + `EndFrame(drawCalls)`,
  read just before EndDraw (Present resets `WprDrawCallsThisFrame`). It trips only after **60
  consecutive drawn frames AND 2 s** in which every frame threw AND issued no draw call. A frame that
  draws, or that throws nothing, resets it; suppressed-draw frames are neutral. So Feed Me Oil,
  Chickens Can't Fly and Brain Challenge (throw but paint) and Galactic Reign-style empty menus
  (draw nothing, throw nothing) never see it. Unit tests: `ErrorScreenTests`.
- **WP8 reports at the stop, not at exit**: `Wp8NativeGame.GuestFailed` fires on the guest thread,
  `ApplicationLaunch` queues `ReportGuestFailure` from it (once; the post-`Run` report is skipped),
  so killing the app from the error screen does not lose the report.
- **Already covered elsewhere, deliberately not duplicated**: a launch that throws before the first
  frame (constructor, `DoInitialize`, a refused runtime type) ends `Run`, and the desktop shows
  `AppRunError`'s message box, Android `GameLauncher.HandleGameResult`'s dialog (driver hint first,
  then the error, truncated at 3,500 chars). Note: a **variable-timestep** game whose Update throws
  is `Exit()`ed by Game.Tick (long-standing), so it reaches those dialogs, not the screen.
- **Report code**: not shown. The hub's code only exists after the launcher flushes, which is after
  the game ends; the screen says "queued … sent when you leave the game" (or that server logging is off).
- **Test hooks (opt-in, env)**: `WPR_TEST_FORCE_FRAME_FAILURE=1` makes every Draw throw before the
  game draws; `WPR_TEST_WP8_STOP_AFTER_FRAMES=N` stops a WP8 guest at frame N. Log lines:
  `[wpr-error-screen] shown: …`, `… Back pressed; leaving the game.`

Verified 2026-10-16 (gamerun, scratch `WPR_DATA_ROOT`): AE 3D Motor forced on D1–D4, Angry Birds
Classic (WP8) stopped at frame 200 on D1, D2 and D4, Back leaves, one hub report. No screen on
Angry Birds, Carcassonne, Flowerz, Cut the Rope, MONOPOLY, MC4, Feed Me Oil, Chickens Can't Fly
and AE 3D Motor run normally. Android Release builds; **not run on Android** (env hooks do not reach
`:game`; a real failing title is the way to see it there).

### Every package format reaches a host; unknown Direct3D/XAML titles infer their page (2026-10-16)

Survey of the library (both roots, 290 distinct packages by ProductID; `scratchpad/formatsurvey`,
read-only, classifies with the installer's own `NativeXamlComponent.Find`). **No appx/appxbundle
exists in the library**; every package is a `.xap`, and every one carries a `RuntimeType` WPR knows.

| format | count | installs as | host | starts? (desktop harness, 2026-10-16) |
| --- | --- | --- | --- | --- |
| WP7 XNA | 172 | XNA | `FnaGameHost` (game's `Game`) | yes - Fruit Ninja WP7 menu |
| WP7 mixed-mode (`SharedGraphicsDeviceManager`) | 13 | Silverlight | `SilverlightGame` | yes - Carcassonne menu |
| WP7 Silverlight | 10 | Silverlight | `SilverlightGame` | yes - Flowerz menu |
| WP8 Silverlight, managed | 2 | Silverlight | `SilverlightGame` | yes - Wordament page |
| WP8 managed + native helper libs (SharpDX, Lumia.Imaging) | 5 | Silverlight | `SilverlightGame` | runs; SharpDX titles draw through `WPR.Engine.HostDirect3D` (Gravity Guy 2 plays); Microsoft Minesweeper stops in its `App` before SharpDX |
| WP8 Unity (managed shell + `UnityPlayer.dll`) | 32 | Silverlight | `SilverlightGame` (UnityPlayer stubbed) | runs, draws black: the engine is native and not hosted. A `wpr-port.json` port is the only route |
| WP8 native exe (`Modern Native`) | 14 | ModernNative | `Wp8NativeGame` (exe) | yes - Angry Birds Rio menu |
| WP8 Direct3D/XAML, background grid | 32 | ModernNative | `Wp8NativeGame` + `XamlInterop` | all reach Connect; 3 hand-written (Alex, MC4, AC Pirates) play; the others run on an inferred page (below) |
| WP8 Direct3D/XAML, `DrawingSurface` | 6 | ModernNative | same, `DrawingSurface` path | reach GetTexture and draw frames; Fruit Ninja and Jetpack Joyride (Mandreel) reach their menus since 2026-10-21 (see "Guest thread N exited...") |
| DRM-wrapped (`PRE\x07`) | 2 | refused `NotDecrypted` | - | BBB: App-ocalypse, Sonic Dash |

GameMaker WP8 exports (Briquid Mini, Lines The Game) are Direct3D/XAML (`WinPhoneRunnerAppComponent`);
the desktop now offers ModernNative ones to Runner.exe too and otherwise runs the ARM runner (Lines:
1,500 frames, black). Packages with no or a broken manifest are the "synthesised metadata" work.

**Three classification bugs, fixed:**

- **`NativeXamlComponent` looked for a `.winmd` named after the component DLL.** WinRT names it after
  the class's namespace (`FusionComponent.FusionMain` in AlexXamlComponent.dll is described by
  `FusionComponent.winmd`). Since the Direct3DXaml-template test (2026-10-14) that sent **Amazing
  Alex**, Fruit Ninja 2013, Jetpack Joyride, Bubble Guriko, Kingdoms & Lords and Make It Rain to
  Silverlight with their game stubbed. It now tries each class namespace and its prefixes, then the
  DLL name. **An Alex installed between 10-14 and now is Silverlight and needs a reinstall.**
- **Metadata now outranks a d3d11 import**: Age of Sparta registers Gameloft's WindowsCorePackage
  toolkit (imports d3d11) ahead of its game.
- **`@AppResLib.dll,-100` titles are resolved at install** (`AppResourceTitle`, the RT_STRING table
  of the package's resource DLL): Microsoft Minesweeper, MONOPOLY ("Millionaire"), Asphalt Overdrive.
  Installed copies keep the raw text until reinstalled; the lists already prefer the catalogue name.

**Generic page descriptions** (`Runtime/XamlShellInference.cs`). A Direct3D/XAML title with no
`XamlShells` entry used to stop at frame 0 ("no page description"). The host now reads the managed
shell's IL with System.Reflection.Metadata (nothing is loaded) and builds a `XamlShell`:

- **Which class**: the one in the package metadata with `CreateContentProvider` that the shell names
  most; calls through one of its interfaces count (EA's shared page holds an `IEAD3DBackground` made
  with `Activator.CreateInstance`).
- **Instance**: `newobj` = ActivateInstance (a constructor with arguments = factory `CreateInstance`),
  a static returning the class (`GetInstance`) = that.
- **Steps**, from the call graph (4 levels) of App `.ctor`/`Application_Launching` + page `.ctor`
  (Launch), page `*Loaded` (Loaded, `OnNavigatedTo` if no Loaded reaches the class), page
  `*OrientationChanged` (after Loaded), `Application_Deactivated`/`OnNavigatedFrom`,
  `Application_Activated`, `*BackKeyPress`, `Application_Closing`. Statics of other package classes
  are included (Doodle God Blitz's `DeviceWrapper.Set*Handler` callbacks: left out, the engine called
  through null), and so is another class built through its factory (Spark2's `LuaMobileSDKBridge`).
  `SetManipulationHost` is added after `CreateContentProvider` (the grid calls it, not the IL).
- **Arguments**: sizes and scale from `Wp8Display`; literal constants the IL loads right before the
  call; a delegate gets a handler whose follow-up calls are read from the C# handler (and any shell
  control it builds - MC4's `VideoPlayer` ends a movie with `SetShowMovieFinish`); device questions
  by parameter or event name (language en-US, region US, firmware 8.0.10521.0, NOKIA, RM-821);
  an orientation by the page XAML's `SupportedOrientations` (read from `.g.resources`); a full-version
  flag true; a component interface gets a default-answering host object.
- **Only the start page** (WMAppManifest's `DefaultTask NavigationPage`) is read when a page of
  that name exists; another page's handlers run only if the app goes there (2026-10-22). Ice Age
  Village's Gameloft Live page put its Loaded calls (`SetWebPageTransition`, Facebook's
  `CancelLastRequest`) into the description; GTA: San Andreas' description lost the second copy of
  every step (not run: excluded from WP8 regression work).
- **A `DispatcherTimer.Tick` handler** the app or start page subscribes is followed, its calls run
  after Loaded. Ice Age Village (`f9d4703c-…`) shows its logo for two seconds and only then calls
  `InitGame` and builds the surface. Without it the game's sound singleton was null, and the first
  map lookup walked a tree at `NULL + 0xF8`: **the zero page is mapped here, so a null object reads
  as zeros instead of faulting, and an all-zero `std::map` walks for ever** (`_stricmp(NULL, …)`
  millions of times a second, frame 0, no error). It now reaches `Connect`, and stops on a later
  defect (below). Measured with the inferred descriptions of all 28 installed components, old vs new:
  only those two titles changed.
- **Back key**: the boolean whose answer goes to `e.Cancel` (closes on false), else the first boolean
  asked (closes on true), else a void call named for Back (keeps every press).

`WPR_WP8_XAML_INFER=1` ignores the hand-written description, which is how this was proved: with it
set, **Amazing Alex, Modern Combat 4 and AC Pirates reach the same screens** (Alex menu, MC4 title,
AC Pirates' intro cutscene) and the contract suite `XamlShellInferenceSuite` checks the inferred
descriptions equal the hand-written ones step by step (MC4 gains its `OnOrientationChanged`). The
log says which: `page description: X (hand-written)` / `page description inferred from the managed
shell: ...`. **Hand-written still wins**; write one when a title needs what IL cannot show (which
branch a handler takes, what a callback really answers).

**`DrawingSurface` providers** (not the background grid): `IDrawingSurfaceContentProviderNative`
(IID `c8377af0-7a69-41d4-a322-8f60e9a0acf8`, found by probing; the IInspectable face at the object's
start is ruled out) gets `Connect(host)`, then per frame `PrepareResources(time, &dirty)` and
`GetTexture(size, &synchronized, &rect)`. The runtime host's `CreateSynchronizedTexture` adopts the
component's texture as the back buffer (`Direct3DRuntime.AdoptPresentationTexture`); both runtime-host
slots answer both methods, telling them apart by whether the first argument is a device resource.

**Also fixed on the way:** a vertex shader that failed on the device left its effect holding a freed
blob, and disposing it killed the whole process (`0xC0000005` in `FNA3D_AddDisposeEffect`, Jetpack
Joyride at teardown); the effect's native handle is now abandoned when the shader is marked broken.

**Where the inferred titles stop** (desktop, 30 s, not chased): Doodle God Blitz 16 frames and Hexic
0 frames (null call in the engine), Asphalt Overdrive 0 frames (null call after its `voidFunction`
delegate; no manipulation handler answers), Bubble Guriko 0 frames (CPU exception 0), Fruit Ninja /
Jetpack Joyride a few frames ("guest thread 2 exited and no thread is runnable"). Asphalt Overdrive
was a `PlatformNotSupportedException` as Silverlight (old tester report), then "no page description"
as native; it now activates, runs its page and connects. Not run on Android (the emulator was in use).

No `ApplicationPatcher.Version` bump: no patcher table changed. Reinstall to pick up the
classification and title fixes; the inference and DrawingSurface host apply on next launch.

**Ice Age Village, still open (2026-10-22):** after `Connect` a ConcRT worker calls
`CreateVertexShader` with a null, zero-length bytecode and then Releases a null buffer (null call
from `0x1001D23F`). The shader file was found and opened (`GetFileAsync`, `OpenReadAsync`), but
the continuation that reads it (QI `IInputStream`/`IRandomAccessStream`, `DataReader`, `get_Size`,
`LoadAsync`, function `0x1001CFC8`) was only scheduled (`_Schedule`) and had not run when the same
worker went on to create the shader. Also: `CreateVertexShader` with no bytecode answers S_OK here
(D3D11 says E_INVALIDARG), and the description calls `SetLaunchFromNotification(True, 0)` and
`SetLaunchFromRemote(True, 0)`, which the real page calls only with a launch query string (the
inference keeps the first of a method's calls, whatever branch it is in).

### A WP8 thread waiting on itself is a Sleep (2026-10-16)

**2020: My Country** (`40c1edab-…`, SDL for WinRT) presented 0 frames for ever on its inferred
page, with ~20M `WaitForSingleObjectEx` + `GetCurrentThread` and ~10M `_Mtx_lock`/`_Mtx_unlock` per
5 s. The store API has no `Sleep`, so SDL's `SDL_Delay` is `WaitForSingleObjectEx(GetCurrentThread(),
ms, FALSE)`. `SyncLibrary.WaitAny` took the `(HANDLE)-2` pseudo-handle as "not a sync object, so
signalled" and returned `WAIT_OBJECT_0` at once without switching. Guest threads are cooperative, so
the `std::thread` worker the main thread had just launched (a 50 ms / 2 ms polling loop) never gave
the CPU back: the main thread never ran again. The inferred page description was fine.

A wait on the current-thread pseudo-handle is now a never-signalled thread object (a thread is
signalled only when it ends): `WAIT_TIMEOUT`, after blocking to the deadline so other threads run.
The `threads:` line shows such a thread as `blocked on thread itself (a sleep)`. **The tell** in an
import trace (`WPR_WP8_IMPORT_TRACE`) is one thread alternating `GetCurrentThread` → `WaitForSingleObjectEx
r0=0xFFFFFFFE r1=<ms>` with nothing else ever running. Desktop D1/D2: tutorial ("Chapter 1 - First
Island"), taps advance it, 60 fps. None of the 11-title WP8 regression set waits on itself (same
frames, same outcomes). Contract suite `SleepOnSelfSuite`. (The `SetViewDimensions(0,0,0,0)` gap
noted here was what broke its touch; fixed in the next section.)

### Inferred page arguments are computed, not guessed; a resource with no initial data is zeroed (2026-10-16)

Two defects in 2020: My Country on the S24, both general.

**Taps did nothing** (the tutorial's Build button, every map tap), while "Click to Continue"
worked, because that one accepts a tap anywhere. The inferred page answered computed arguments by
parameter name: `SetViewDimensions(0, 0, 0, 0)` and `set_ScaleFactor(160)`, where the page passes
`(0, 0, 768, 1280)` (`Floor(Host.Content.ActualWidth * ScaleFactor / 100 + 0.5)`, minus a tray
height) and `Host.Content.ScaleFactor / 100f` = **1.6** (Amazing Alex passes the raw 160, so no
name rule can be right). The engine scales every pointer by them, so each touch landed off the
screen. `XamlShellInference.Arguments.cs` (`ShellReading.EvaluateArguments`) now runs the calling
method's IL from its start to the call on a small abstract machine: constants, locals, arithmetic
with CLR int/float semantics, conversions, `Math.Floor/Ceiling/Round/Min/Max/Abs`, `new Size(w, h)`,
`System.Windows.Interop.Content.ActualWidth/ActualHeight/ScaleFactor` from `Wp8Display` (portrait,
as on WP8 in any orientation) and `SystemTray.IsVisible` from the page XAML; a branch on a known
value is taken, on an unknown one falls through. A value it cannot know stays null and the old
by-name answer is used. The contract suite now checks every numeric argument of the three
hand-written titles equals the inferred one, and My Country's two (when installed, real data root
or `WPR_DATA_ROOT`). **The tell**: a title whose "tap anywhere" screens advance but whose buttons
never respond; read the `[xaml] SetViewDimensions(...)`/`set_ScaleFactor(...)` lines.

**Thin light seams round every map tile and sprite** (D3D11, Vulkan and GLES alike, so not a
driver or DXT problem). The game packs sprites at run time into a 1024x1024 atlas it creates with
**no initial data**; a texture's storage is a guest heap block, and a reused block still held an
old loading screen, which linear filtering pulled in at each sprite's edge. D3D11 hands out such a
resource zeroed: `Direct3DRuntime.ZeroStorage` now clears textures and buffers created without
initial data when their block was reused (`ArmEmulator.LastAllocationReused`; the heap's untouched
end is zero already, and clearing every new block cost AC Pirates ~5% of its loading frames)
(`Direct3DStateSuite`). **The tell**: lines along the edges of sprite quads, partial
and light; dump the atlas and look between the sprites.

Desktop D1/D2/D4 (+ `WPR_FORCE_NO_DXT=1`): tutorial → Build → Eagleroost Offices → placement
(confirm/cancel shown), no seams. WP8 regression set (11 titles, Terraria/Halo/GTA excluded): same
outcomes, frame counts within run-to-run noise. Not run on a phone or the emulator.

### "Guest thread N exited and no thread is runnable" (2026-10-21)

The top WPR Hub crash group of 0.2.06 (Fruit Ninja 2013, Jetpack Joyride, Halo: Spartan Assault, all
on Android). It had two separate causes behind one message. Neither was a lost wake-up in a sync
primitive: `SetEvent`, the waits, `_Cnd_*` and ConcRT were all correct.

**A timed wait is not a deadlock** (`GuestThreads.cs`, Halo). FMOD sleeps with
`WaitForSingleObjectEx(event, 2 ms)` on an event nobody sets (0x0E000007 in Halo, from FMOD code at
0x1005EE61, about 130 times in two minutes on the main thread; its pool threads 8 and 45 park on it
too). `OnThreadExit` asked `PickNext` once. A worker that ended inside those 2 ms found nothing
runnable *at that instant* and stopped the run, although the main thread would be runnable a
millisecond later. Android's slower clock widens the window, which is why every report came from
there. Now:

- `BlockCurrentThread(..., timeout:)` records when a timed wait runs out (`GuestThread.WakeAt`).
  The four timed waits pass it: `WaitFor*Object(s)Ex`, `SleepConditionVariable*`, `_Cnd_timedwait`
  and `_Mtx_timedlock`.
- A thread that ends with nothing runnable waits, in real time, for the earliest of those deadlines
  (`NextAfterExit`; `EndCurrentGuestThread` too). If deferred work is queued, it runs it on the
  ending thread first, as a wait with nothing to switch to already did. A deadline that passed
  100 ms ago while its thread still cannot run (a condition wait that cannot retake its lock) is
  dropped, not waited on for ever.
- Untimed waits only, and nothing queued, is still reported as a deadlock with the same message.
- `BlockCurrentThread`'s own "nothing else can run" path is unchanged: that thread still times out
  or carries on at once.

The tell is in the `threads:` lines: the main thread `blocked on event X`, with X also named by
threads that are idle in a healthy run. Confirm with a one-off `Console.Error` line in `WaitAnyBlocking`
printing the main thread's handle and timeout: a small finite timeout means it was this.
`TimedWaitsWaitedOut` counts the rescues.

**Mandreel's managed message thread** (`MandreelMessages.cs`, Fruit Ninja 2013 and Jetpack Joyride,
both `__MandreelWP8Direct3DInterop`). The page's Loaded handler starts
`new Thread(alpha.Beta).Start()` just before `CreateContentProvider`. `Beta` loops on the component's
`wait_event(out size)`, reads the message (`get_message_event_data`/`2`) and answers questions with
`set_message_event_data`. The native side posts with `SetEvent(request)` and blocks in
`WaitForSingleObjectEx(reply, INFINITE)`. The inferred page never ran that thread, so the first
message deadlocked. While no other thread existed the wait timed out (the "nothing else can run"
path), which is why two messages appeared to work. Then a ConcRT worker drained its queue and
exited. Now the inference sets `XamlShell.MandreelMessages` when the class has the five members, and
the host starts a guest thread (`Mandreel messages`) that plays `Beta`:

| message | page | host |
| --- | --- | --- |
| 1, command (`ProcessMessage`) | UI work through the dispatcher | `reset_NavigatedTo` clears the flag; `PromptExit` is the quit dialog's OK, so the app closes (`BackPressDelivered(false)`); `save_picture`, `purchaseGame`, `PromptUpdate` and `open_*` links are logged and dropped |
| 2, `dispatch_async` | `dispatch_async_uithread(idx)` on the UI thread | the same call, queued as a follow-up step for the main thread's next frame |
| 3, question (`ProcessMessage2`) | a string answer | `get_language` en, `get_music_state` 0, `get_deviceId` a fixed Base64 id, `check_NavigatedTo` 1 until reset (again after a return from the background), `check_promptExit` 0, `licenseStatus` 1, `check_LicensePurchaseResult` 2, anything else `error` (the page's default) |

The thread has its own out-parameter scratch: the host's `_scratch` belongs to the main thread,
which may be mid-call. Log: `[xaml] mandreel: message 3 "get_language" -> "en"` (first three of each).

Contract tests: `Suites/ThreadExitSuite.cs` covers a timed wait outlived, a signalled one resumed at
once, a real deadlock still reported, and the Mandreel flag on every installed Mandreel title (2306/2306).

Verified on the desktop, D3D11, scratch `WPR_DATA_ROOT`:

- **Jetpack Joyride**: was 5 frames; now its menu ("touch anywhere to play"), then a run with the
  distance counter climbing, at 60 fps for 40 s. The run's world art does not draw; that is a
  separate problem.
- **Fruit Ninja 2013**: was 8 frames; now its main menu at 60 fps.
- **Halo**: menu → START → operation → mission → two thumbs → play ("Survive the Covenant
  invasion"), 16,682 frames.

The desktop never reproduced Halo's stop: the window is too narrow there.

| cell | Fruit Ninja 2013 | Jetpack Joyride | Halo |
| --- | --- | --- | --- |
| D1 D3D11 | ✅ menu, 60 fps | ✅ menu → run (world art missing) | ✅ mission played |
| D2 Vulkan | ✅ menu | ✅ menu → run (world art missing) | not run |
| D3 OpenGL | ✅ menu | ✅ menu → run, world drawn | not run |
| D4 GLES 3.0 + no DXT | ✅ menu (washed out, as D4 always is) | ✅ menu → run, world drawn | not run |
| A1/A2, P1–P3 | not run (no device or emulator attached) | | |

WP8 regression set (12 titles, Terraria/Halo/GTA excluded), HEAD vs this change, 30 s each: the
same outcome for every title. Ice Age Village is 0 frames on both. Frame counts first differed by
10–20%, with the change run first while other builds were going; re-run alone they matched (Classic
1580/1563, Seasons 1496/1498, Stella 1567/1567).

**Open**:
- ~~On a 1280x768 desktop window, both Mandreel titles draw into a 480-wide strip at the left.~~
  ~~Jetpack's world art draws only on OpenGL.~~ Both fixed 2026-10-23, see the next section.
- The Mandreel thread is a host stand-in for code in the game's own package (the managed page).
  The rule is to run shipped libraries as shipped; it is kept by decision until that page can run.

### Mandreel titles play: a landscape DrawingSurface, a palette read past its end, Shared\ShellContent (2026-10-23)

Jetpack Joyride and Fruit Ninja 2013 draw through ANGLE (their own `libGLESv2.dll`, ARM) into a
`DrawingSurface` on a `SupportedOrientations="Landscape"` page. Three runtime defects, general ones:

- **A relatively indexed palette with a register read directly above it.** Jetpack's sprite batcher
  (vs_2_0) reads `c2[a0.x]` and `c122` directly, from one constant buffer. `Aon9Shader.ConstantArraysFor`
  made `c2..c122` one indexed array, so `c122` was a direct read inside it: MojoShader's HLSL emits that
  as `#define vs_c122 vs_uniforms_vec4[i];` (the stray semicolon fixed in the vendored source but not
  in the shipped `FNA3D.dll`), the compile failed and the replay skipped every sprite draw
  (`Could not compile input layout`, `vertex shader … failed on the device`). Vulkan drew nothing
  either, silently. The indexed run now ends at the first directly read register above its base
  (`ConstantReads` tells relative from direct, skipping a relative source's address token). **The
  tell**: text draws, images do not, on D3D11 and Vulkan only; `WPR_WP8_SHADER_DUMP` + a d3d9
  disassembly shows a `cN[a0]` and a direct `cM`, M > N, in the same buffer.
- **The surface is landscape.** `XamlShell.Landscape` (from the page XAML) and `PresentsUnturned`
  (`DrawingSurface && Landscape`): `GetTexture` is handed 1280x768 rather than the grid's 768x1280,
  the component's texture is presented `PresentTurn.None`, and pointer positions are landscape
  (`PointerRotation = "none"`), because a DrawingSurface is an element of the rotated page. A
  background grid is always the portrait screen and is unchanged.
- **`Local\Shared\ShellContent` must exist.** A phone installs every app with it (and
  `Shared\Transfers`); Mandreel writes all its saves there without creating it, so every write was
  ENOENT and nothing survived a relaunch. `FileLibrary` creates both (`FileLibrary.SharedFolders`).

Contract suite `MandreelSurfaceSuite`. Verified D1–D4: Jetpack menu, missions, a run (flying, coins),
death, results, Back, best distance after a relaunch; Fruit Ninja menu → mode select → a Classic round
to its score screen. Galaxy S24 (P1, Vulkan, Release APK 0.2.08 / versionCode 51 installed in place):
Jetpack fills the 2340x1080 screen, a run flies and collects coins at ~59 fps, no thrown frames.
Not run: P2 (S24 GLES), A1/A2, Mali. No `ApplicationPatcher.Version` bump and no reinstall.

**Then, same day: a memory leak and Xbox sign-in.** On the S24, managed memory climbed 39 → 460 MB in
two minutes and replay time 2 → 8 ms, felt as lag. Three things retained everything: the software
draw list (`FrameCapture._draws`) is emptied only by `ClearRenderTargetView`, and ANGLE never clears
the back buffer that way, so in GPU mode every draw with its vertex copies stayed alive (330,000
after 100 s); `DeliverFrame` now ends it when the GPU path took the frame and no software picture is
wanted. `FrameCapture.RgbaCache`/`BlockCache` (now `TextureImageCache`) and the replay's host
textures are dropped after 10 s / 600 frames unused, because a guest texture is never destroyed
(`Release` only counts). Desktop, 150 s: 50-110 MB flat, GC pause per 5 s 25 → 6 ms. The `guest fps`
line now carries `longest-gap`, `hitches>50ms` and `gc-pause`. What is left is one ~120 ms gap per
run end (the game's own sync and save work) and on new level art. **Xbox**: `IUserStatus.IsOnline`
answered false, which Jetpack reads after `SignInAsync` as "Xbox not signed in"; it is now true while
an Xbox Live host is attached (the Spark2 `IsInternetAvailable` rule). Achievements list from the
catalogue, results post `BestScore1:0` (distance) / `BestScore2:0` and read back from WPR Hub.

**Then: friends, leaderboard rows, frame pacing, allocation (versionCode 59 on the S24).**
- **A leaderboard row for a stranger crashed Jetpack** (null call from `0x101A4263`, its own
  leaderboard code): it looks every row's player up in the friends list it fetched at sign-in
  (`GetFriendsAsync`, which answered empty) and calls through what it finds. `GetFriendsAsync` now
  answers the player's WPR Hub friends (`IXboxLiveHost.GetFriends`, `OnlineBackend.Invites`), and a
  title that has fetched friends gets rows only for the player and those friends
  (`RowsTheTitleKnows`); a title that never asks (the Rovio titles) keeps every row. To find this,
  `PhoneDLL1.dll` (Jetpack's Xbox glue) was mapped at `0x10630000`: the summary now prints
  `modules: mapped …` so post-mortem addresses can be put back in the right DLL.
- **Pacing**: the guest was woken per host `Update`, and FNA's fixed timestep beats against a
  display that is not exactly 60 Hz (the S24 reports 60.000004): every ~1.7 s two Updates ran before
  one Draw, the guest built two frames back to back and one was never shown. It is now woken per
  presented frame (`EndDraw`), with a frame debt (+1 per catch-up, -1 per 60 draws, an extra wake at
  2, capped at 4) so a host that keeps falling behind still runs the game at its speed. `host
  updates=/draws=` on the `guest fps` line shows the beat.
- **Allocation, 22 → 5 MB/s**: `UpdateSubresource` copied guest-to-guest through a new array
  (`CopyMemory` now); constant-buffer copies are pooled (`TakeConstantArray`, retired with
  `GpuFrame.Serial` and reused once the host reports the frame replayed); a split constant table's
  arrays get scratch `Vector4[]`; a draw binding the same copies reuses the previous map. Phone gen0
  collections ~8/s → ~2.5/s. `System.GC.Gen0Size` via `RuntimeHostConfigurationOption` had no effect
  on Android (tried, removed). `file-io=` (time and calls in file handlers) joined the `guest fps` line.
- Left: one 50-60 ms gap every few seconds on the phone (a missed display refresh, not GC, not file
  I/O) and ~120-500 ms at a run's end (the game's own sync and save, ~600 file calls).

### Update checks and the About page's "what's new" (2026-09-29, Android)

`WPR.Shell.AppUpdates` asks GitHub's `/releases/latest` (drafts and pre-releases excluded) at most
every 12 hours, compares versions number by number (`0.1.10` > `0.1.09`; the tag's `v` and zero
padding mean nothing), and remembers what it found in `<DataStore>/Online/update-check.json`.
Android calls it from `MainActivity` (end of start-up and every resume) through
`Native/UpdateNotifier`, which posts one notification per release on channel `wpr_updates`; tapping it
opens About. `Configuration.UpdateCheckEnabled` (null = on, "UPDATES" switch in settings) stops only
the automatic check. The About page's update row always works: it downloads the release's `.apk`
asset in the browser, or checks now. Log lines are `[wpr-update]`.

About is two pivots, **about** and **what's new**. Credits and the shell blurb were removed.
What's new renders `Docs/ReleaseNotes/<version>.md`, which is bundled as `assets/ReleaseNotes/`
(`*-full.md` excluded) so it works offline and matches the installed build. `Native/ReleaseNotesView`
reads the small Markdown subset those files use. The installed version is shown in full (a dev build
gets the newest notes at or below its version), and earlier versions expand in place. **So a
release's notes file must exist before the APK is built**, which release.yml also reads (as Highlights) when present.

No desktop equivalent yet; `AppUpdates` names no UI type, so it is ready for one (`InstallerUrl`).

### Home-screen game shortcuts go through a trampoline, not GameActivity (2026-09-05)

WP7's "pin to start" for the Android home screen: long-press a game in the games list →
`pin to start` → one launcher icon carrying that game's tile art and name, which starts it
without the WPR shell appearing on the way. Android only.

**The shortcut carries a ProductId and nothing else, and it points at
`Native/GameShortcutActivity`, never at `GameActivity`.** Pointing it straight at the game
activity is the obvious shape and it is wrong three times over — a shortcut lives on someone's
home screen for months, so every one of these is a real failure:

- **A serialised `Application` goes stale.** `GameLauncher.Launch` re-patches an install whose
  `PatchedVersion` is behind `ApplicationPatcher.Version` *before* launching. A snapshot in the
  intent would skip that and TypeLoadException the next time the patcher table changes.
- **Native ports never reach `GameActivity` at all** — `LaunchUnityPort` starts a different
  activity or package.
- **A dead game process reports its reason through `onActivityResult`**, which needs a caller.

So the trampoline resolves the id against `ApplicationContext` and calls the same
`GameLauncher.Launch` the games list calls. It is the *only* launch path a shortcut may take.

**Its own `TaskAffinity` (`com.wpr.android.shortcut`), plus `AutoRemoveFromRecents`.** Without the
affinity the tap brings the launcher's task forward — Start screen first, then the game, and back
to the games list afterwards. With it, the shortcut task holds only the trampoline and the game, so
finishing returns to the home screen. `GameActivity` is started **without** `NEW_TASK` and
therefore joins that task, which is what keeps `startActivityForResult` working across the process
boundary. The shortcut intent itself carries `NewTask | ClearTask`: without `ClearTask`, tapping a
shortcut while another game is still up resumes that task as it stands and hands back the *running*
game instead of the one asked for.

**`Exported = true` is required, and it is not redundant.** From API 26 the system starts a pinned
shortcut on the publisher's behalf, so exporting would not be needed — but this app's minimum is
21, `ShortcutManagerCompat` falls back to the legacy
`com.android.launcher.action.INSTALL_SHORTCUT` broadcast below 26, and the launcher then starts the
intent *as itself*. That fallback is also why the manifest declares that permission: without it
`isRequestPinShortcutSupported` returns false on 21–25 and the menu entry simply never appears.

**When the trampoline finishes is a two-flag rule, and the naive versions are all broken.**
`OnStop` latches `_HandedOver`; `OnResume` finishes if it is set. That covers a game exit, a native
port closing, and anything else that ever gave the screen back — with no need for `Launch` to
report what it did. The exception is the failure dialog, which `HandleGameResult` shows **on this
activity**: `onActivityResult` runs just *before* `OnResume`, so it latches `_ShowingError` and the
dialog survives. Finishing from `OnActivityResult` instead would kill that dialog on the way up.

That is the one thing this feature changed outside itself: `GameLauncher.HandleGameResult` and
`ShowError` gained an optional `onErrorAcknowledged` / `onDismissed` callback. It hangs off
`Dialog.DismissEvent`, **not** the OK button — Back and a tap outside close an `AlertDialog`
without the button ever firing, and either one would stranded the trampoline on a blank screen.

**The icon is composed, not the raw tile.** `GameShortcuts.Frame` draws the tile scaled to fit the
adaptive-icon **72-of-108 safe zone** on the live accent. Full-bleed is the tempting alternative
and it crops: a WP tile is square and the launcher's mask is not. At 72/108 the art is exactly the
size of a normal app icon's, with the accent filling the mask around it; a circle mask still clips
the tile's own corners, which WP art tolerates because its content is inset. Bitmap size is the
launcher's own icon size × 108/72, clamped by `ShortcutManagerCompat.GetIconMaxWidth/Height`
because it crosses to the launcher over IPC. `CreateWithAdaptiveBitmap` unconditionally —
`IconCompat` does its own rounding below 26, so there is one code path.

**A pinned shortcut cannot be deleted by the app that published it, only disabled.** So
uninstalling calls `GameShortcuts.Retire`, which `DisableShortcuts` it with a message; the
trampoline does the same for a row that vanished some other way. Skipping this leaves a live tile
that launches a product id with nothing behind it.

**Two things deliberately not built.** Dynamic shortcuts (long-press the WPR icon for recent
games) — different feature, and this one was asked for. And repainting pinned icons when the accent
changes: `ShortcutManagerCompat.UpdateShortcuts` would do it, at the cost of the Settings page
reaching into the shortcut store on every write, so a shortcut keeps the accent it was pinned with.

`GameTileArt.Decode` came out of `GameListAdapter` so the list and the shortcut resolve a game's
art the same way — through `GameIconStore.Resolve`, which is the shared rule and is **not** just
`Application.IconPath` (that names a file inside the install folder). Named for the art rather than
the file so it does not read as a second icon store beside that one; the adapter keeps its
per-product cache, which is its own concern (it runs on every fling frame).

No `ApplicationPatcher.Version` bump and no reinstall — no patcher table changed and no IL is
rewritten. **A manifest change, though**, so this one needs a reinstall of the APK rather than just
a rebuild.

**Not built for Windows.** The desktop head could pin a Start Menu / desktop shortcut with the same
`ProductId`-to-launch shape, and nothing here blocks it, but this is Android-only today.

### Touchscreen and hardware buttons are NOT sensors (2026-09-02)

> **Partly superseded by the section above.** The conclusion that touch needs no *platform seam*
> still holds — SDL supplies it identically on both heads. What changed on 2026-09-03 is that
> synthetic touch injection was built, exactly as the "where the gamepad→touch mapper goes" note
> below predicted, and the hardware Back key became rebindable.

Asked as "split sensors into accelerometer / touchscreen / hardware button". Two of those three
are not parts of the sensor bucket, and building them as such would have recreated the
`WPR.Abstractions` mistake. Recording the answers so they aren't re-litigated.

**Touch gets no seam, and that is a considered decision.** Three facts:

- Touch arrives from **SDL identically on both heads**. There is no per-platform variance to
  abstract, so an `ITouchProvider` would have one implementation forever.
- The pull side is *already* seamed — `IInputBackend.GetTouchCapabilities()` /
  `UpdateTouchPanelState()` — and `IInputBackend` is permanently pinned in `WPR.Framework.Xna`
  by naming `Keys` / `GamePadState` / `TouchPanelCapabilities`.
- The push side is *already open*: `WPR.Framework.Xna.csproj` grants `InternalsVisibleTo` to
  **`WPR.Backend.FNA`**, so backend code can already call `TouchPanel.INTERNAL_onTouchEvent` /
  `SetFinger` / `EnqueueGesture`. **Nothing needs building to open the gamepad→touch path.**

**Trigger to revisit:** a head that gets touch from somewhere other than SDL — a native Android
`MotionEvent` path, or a UWP/WinUI head.

Also: the Silverlight pointer pipeline (`SilverlightTouchRouter` + `Gestures.cs`) is a *separate*
recognizer from XNA's `GestureDetector`. Since 2026-10-13 it is fed from `TouchPanel` samples by
`SilverlightGame` (it used to run over Avalonia pointer events in `PhoneApplicationFrameView`,
deleted with the Avalonia host), but it still produces Silverlight routed events and toolkit
gestures, not XNA gestures. Two recognizers by design — don't "unify gestures".

**The hardware Back press is on `IGameHost`**: `PressBackButton()`, implemented by `FnaGameHost`
over `WprPhoneBackButton`. Before this, `GameActivity` held a `FnaGameHost` typed field purely to
reach `PressPhoneBackButton()`. **Back only** — WP7's Start and Search deactivate the app rather
than reaching the game, which is why the Silverlight bezel wires them as no-ops and why the other
two would be members no host could implement. A future "Start button means Back" input binding
still just calls this.

**The XNA and Silverlight Back paths stay separate**, deliberately: one is a level-sampled gamepad
button held for a frame, the other is a routed event a page can cancel before `GoBack()`.
Silverlight apps aren't driven by `IGameHost` at all and there is no Silverlight path on Android,
so a unifying interface would have two implementations and no polymorphic caller. (Superseded in
part, 2026-10-13: every Silverlight title now runs in `SilverlightGame`, which reads the pad's Back
and routes it into `PhoneApplicationFrame.HandleBackKey` — the two paths meet there, not in a seam.)

**A live bug fixed on the way past.** `SDL2_FNAPlatform.GetGamePadState` consulted
`PhoneBackButtonPressed` **only** inside its `device == IntPtr.Zero` early return. Once any
controller was connected it built state purely from SDL and never looked again — so Esc,
`SDLK_AC_BACK` and `WprPhoneBackButton.Press()` were all silently dropped. On Android the
hardware Back key stopped reaching the game the moment a Bluetooth pad connected. It now ORs
`Buttons.Back` into `gc_buttonState` as well; OR rather than assign, because a real pad's
Select/View already maps there and the two sources must not cancel out.

**Where the eventual gamepad→touch/gesture mapper goes.** Designed, deliberately not built — the
injection path is already open, so building early buys no optionality. The full design (and the
two non-obvious ordering traps that break a naive attempt) is a remarks block on
`TouchPanel.Update()`, which is where the next implementer will actually be looking. The
headlines: it is a **decorator over `IInputBackend`**, *not* a `GameComponent` — components run
before `FrameworkDispatcher.Update()` and the SDL drain wipes their finger-state writes in the
same tick, so **the tilt-emulator precedent does not transfer**. It needs **no head-side seam**
either: `IKeyboardEmulationHost` (then `ITiltEmulationHost`) exists only because `KeyboardTiltBindings` resolves persisted key
names against `Avalonia.Input.Key`, and gamepad triggers are `Buttons`, an XNA enum with no
Avalonia twin — so the profile is plain data and the translator lives entirely in the backend.
And it is **not** an `IPlatformCapabilities` member: it works wherever a gamepad is, on both
heads, from the same code. It is not a fact about the device.

**The keyboard-tilt emulator is split across the seam too** (2026-09-01, Stage 5). Its two moving
parts are XNA `GameComponent`s — a polling input component on the game's own `Update`, and the
optional dial overlay on `Draw` — so they have to derive from spine types and therefore have to
live in a backend (**superseded 2026-09-20** — the spine is the framework's now, and they live in
`Src/Engine/WPR.Engine.Input/`; see "The launch sequence and input emulation are engine code" below).
They were `WPR.Backend.FNA/Input/`, attached by `FnaGameHost` when a head
registered an emulator; before that they were in the Windows head, which is precisely why that
head was in `KnownBackendLeaks`.

Above them sits everything that knows what a key *means*: `WPR.Xna.Rhi.IKeyboardEmulationHost`,
implemented by `KeyboardEmulationHost` over `KeyboardTiltBindings` +
`KeyboardAccelerometerHost`. The split is **mechanism below, meaning above**: the backend polls
`Keyboard.GetState` and resolves the presentation orientation, then reports both as raw facts; the
implementation resolves bindings, does edge detection and drives its own accelerometer host.

**Superseded in one detail on 2026-09-02:** the "meaning" half is no longer *head* code — it is
`Src/Modules/Input/WPR.Input.Keyboard` (see "Input implementations are modules" above). The
justification recorded here was that the binding table is shared with the Silverlight host and so
had to resolve `Avalonia.Input.Key`. That was true of the *parameter type* only; the method body
never touched Avalonia, and collapsing it to `ResolveKeyName(string)` removed the tie entirely.
Only the Avalonia `TiltOverlay` control stayed behind.

Android registers nothing (it has a real accelerometer), so the backend attaches no components —
the same "absent means unavailable, never an exception" degradation as the achievement store.

### `Window.ClientBounds` is the WP7 screen, not the host window — and it is PORTRAIT (2026-09-18)

`GameWindow.ClientBounds` returned `FNAPlatform.GetWindowBounds(window)` — the real SDL window,
1200x720 on this desktop and the whole panel on a phone. On WP7 the window **is** the screen, and
that screen is a fixed 480x800 WVGA panel that never rotates. It now returns
`Rectangle(0, 0, 480, 800)` unconditionally, from `WPR.Framework.Xna/GameWindow.cs`.

**The asymmetry is the whole point, and it looks like a bug until you check it.** A landscape title
gets an **800x480 backbuffer** and **800x480 touch coordinates** while `ClientBounds` keeps saying
**480x800**. Games were written against exactly that.

**Gravity Guy** (`4f930d12-…`, Miniclip) is the reference case and the reported symptom was "the
menu won't take clicks". Its Cocos2D port (`Libs.dll`, `XNAGlue.UpdateMouseTouch`) draws into a
480x800 portrait logical space and rotates every incoming touch into it with

```csharp
if (hackPortrait) { float t = mx; mx = game.Window.ClientBounds.Width - my; my = t; }
```

With the true WP7 value that maps the 800x480 landscape touch rect **exactly** onto the game's
480x800 space — bijectively, no clipping, both scale factors literally 1. With 1200 it puts `mx`
in (720,1200] against a 480-wide space, so **every touch lands off-screen** and no menu item can
ever be hit. The game itself confirms the model: `MyDLCManager` switches between
`BackBuffer 800x480 + TouchPanel.Display 800x480` (landscape) and `480x800 + 480x800` (portrait),
so 480 can only have come from the screen, not the backbuffer.

**There is nothing to grep for.** No exception, no warning, not one `[wpr-fce]`; the game renders,
animates, plays music and logs a clean launch while being completely dead to input. The only way in
is to read what the game does with the coordinate — the `TouchPanel.GetState` traces show correct
800x480 positions arriving, which is exactly why they mislead. **"Renders perfectly but ignores
every tap" means a coordinate transform above the seam, not a touch-delivery bug.**

**Expect this to have fixed more than one game.** The byte-identical expression ships in Miniclip's
`LibsC.dll` — verified by decompiling **Fragger**, **iStunt 2** and **Monster Island**. Across the
307-XAP library exactly **14** titles read the property at all (the others: Crackdown 2, Farm Frenzy
2, Fling, Fusion Sentient, Gravity Guy 2, Hydro Thunder GO, PES 2011/2012, Puzzle Quest 2, The
Harvest), so the blast radius either way is small and enumerable.

**Anything that genuinely means the OS window uses `GameWindow.HostClientBounds`** —
`internal abstract`, implemented by `FNAWindow`, reachable via the existing `InternalsVisibleTo`.
Three call sites were moved to it and are **byte-for-byte unchanged in behaviour**:
`GraphicsDeviceManager.INTERNAL_OnClientSizeChanged` (feeding it the phone screen would flip a
landscape game's backbuffer to portrait on the first resize), `EndScreenDeviceChange(string)`, and
`TiltInputXnaComponent`'s pre-device orientation guess (which would otherwise answer Portrait for
every game). **So the only behavioural change in the repo is what a game sees.**

Verified on Windows: title screen → main menu → OPTIONS → BACK → PLAY → SINGLE/MULTI PLAYER, all
responding; Mirror's Edge and Fragger unaffected/working. **Android is fixed by the same change**
and for the same reason — the surface there is the device panel, never 480x800.

No `ApplicationPatcher.Version` bump and no reinstall — this is shim behaviour in
`WPR.Framework.Xna`, so games pick it up on next launch.

### A landscape picture drawn into a portrait backbuffer turns the desktop window (2026-10-23)

**Need for Speed Undercover** (`09cc7f9c-…`, EA) was sideways on Windows: the whole picture, the
intro movie, the menus, the race and its HUD. Nothing in WPR answered it wrongly. The game is an
EA J2ME port: its `midp` layer asks for a **480x800** backbuffer, leaves `SupportedOrientations`
alone, hardcodes `isLandscape = true` in `midp.Display`, and draws every sprite batch through
`CreateRotationZ(PI/2) * CreateTranslation(480, 0, 0)`. On the phone the player turned the
device. A desktop window cannot be turned, so the window turns itself now. **The Sims 3**
(320x533) and **The Sims Medieval** ship the same layer; The Sims 3 was installed and run (it
turns to a 533x320 window). Medieval has not been run.

- **Detected, not listed** (`WPR.Xna.Rhi.PresentationTurn`, `Backend/PresentationTurn.cs`). Such a
  game says nothing through the XNA API that a portrait game does not; what it draws gives it
  away. `SpriteBatch.Begin` reports every batch that starts on the game's portrait backbuffer
  (`GraphicsDevice.WprNoteSpriteBatch`); `QuarterTurnOf` calls its matrix a turned picture only
  when it is an exact unit quarter turn that carries the landscape rectangle onto the backbuffer
  (`+90° then x + width` = Anticlockwise, `-90° then y + height` = Clockwise). A sprite turned
  about its centre, a scaled turn or a camera pan is not. `LauncherMovieOverlay` reports the frames
  where it turns a landscape movie onto a portrait backbuffer. **10 turned frames in a row turn the
  window, 180 frames in a row whose backbuffer batches are all upright turn it back**, and frames
  with no backbuffer batch (a 3D scene alone, a pause) count for neither. The rule is
  `PresentationTurn.Observe`, unit-tested. Only on a platform that cannot rotate
  (`SupportsOrientationChanges` false: the desktop) and only while the backbuffer is portrait.
  Log: `[wpr-display] 480x800 backbuffer shown turned Anticlockwise on a 800x480 window` and
  `… shown upright again` (stdout, through `XnaBackend.LogInfo`).
- **NFS opens on its own EA splash for about a second in a portrait window** (that splash is drawn
  upright, so it is sideways art), then the window turns as the intro movie starts and stays
  turned. A portrait game that plays a landscape movie through `MediaPlayerLauncher` now gets a
  landscape window for the movie and its portrait window back about 180 frames after it.
- **`GraphicsDevice` keeps the game's backbuffer as a render target** (`wprTurnTarget`) and binds it
  wherever FNA binds the real backbuffer, so `PresentationParameters`, the viewport, clears,
  `GetBackBufferData` and `GetRenderTargets` all stay the game's. FNA3D is given the parameters
  turned, and `Present` draws the target onto the real backbuffer turned a quarter with an internal
  `SpriteBatch`, restoring every device state it touches. The achievement pop-up and
  `FrameCapture` run **after** that and size themselves from `GraphicsDevice.WprPresentedBounds`
  (the host backbuffer during a turned present), so both work in what the player sees (a harness
  `shot` of NFS is 800x480, upright). The game's `PresentationParameters` are never altered: a
  first version swapped them for that stretch, and another thread of the game can read them at
  any moment.
  A change of decision is applied after a present (`WprEndFrame`: `ResetBackbuffer` with the new
  shape, no `DeviceReset` raised to the game), and the next `PollEvents` reshapes the window through
  `GameWindow.EndScreenDeviceChange`, which `FNAWindow` sizes turned.
- **Input goes the other way**: `PresentationTurn.WindowToBackBuffer` in `Mouse.GetState` /
  `SetPosition` and in every SDL mouse-as-touch and finger path of `SDL2_FNAPlatform`
  (`WprWindowToDisplay`, `WprFinger`). **NFS reads one finger through `Mouse.GetState`**, not
  `TouchPanel` (`JavaLib.Update` calls `HandleMouse()` with fewer than two touches down), so the
  harness `tap` (a synthetic `TouchPanel` finger) does nothing in it. A local `mclick u v secs`
  verb (window-normalised, a `DispatchProxy` over `XnaBackend.Input`) drove it instead.
- `Window.CurrentOrientation` stays `Portrait`, as WP7 reported. The keyboard tilt emulator treats
  a turned window as **LandscapeLeft** (`TiltInputXnaComponent.ResolveOrientation`): NFS steers on
  accelerometer Y.
- **Android is unchanged on purpose**: the activity stays portrait and the player turns the phone,
  which is the WP7 behaviour.

Verified 2026-10-23 (desktop harness, scratch data root, window-space `mclick`s):

| cell | NFS Undercover | The Sims 3 | The Sims Medieval |
| --- | --- | --- | --- |
| D1 D3D11 | ✅ movie, dialogue, race, tilt steering, brake | ✅ menu → Select Game → Create A Sim | ✅ menu (landscape backbuffer, never turned) |
| D2 Vulkan | ✅ same | ✅ same | ✅ menu |
| D3 OpenGL | ✅ same (hung before) | ✅ same | ✅ menu |
| D4 GLES 3.0 + no DXT | ✅ same (hung before; washed out, as D4 always is) | ✅ same | ✅ menu |
| A1/A2, P1–P3 | not run: Android behaviour is unchanged by design | | |

Medieval's play button lights under a tap but the scripted press did not start a game; not chased.
Regression, D1, this build against the unchanged base: Mirror's Edge, Angry Birds, Doodle Jump
(portrait), Gravity Guy, Fragger and AE 3D Motor reach the same screens. Unit tests:
`PresentationTurnTests`, `DeviceThreadDispatchTests`.

**Desktop OpenGL deadlocked NFS, before this change too; a worker's resource calls are now
posted, not waited for.** Its `QuadEffectManager` guards the scene with a homemade spin-lock
(`while (m_threadLock) Thread.yield();`, and midp's `yield()` is empty). The loader thread took
the lock and created a texture; on desktop OpenGL that call is handed to the device thread
(`DeviceThreadDispatch`) and the worker waited, while the device thread spun on the lock inside
`SceneMenu.updateIntro`, calling nothing WPR could drain from. Android's GLES creates textures on
FNA3D's worker contexts and never had it; the Windows `FNA3D.dll` cannot be rebuilt here (no MSVC)
to get them. So on desktop OpenGL (`FnaGraphicsBackend.MarshalPooledResourceCalls`: off the device
thread, not Android):

- **Texture (not render target) and vertex/index buffer creation is posted**
  (`DeviceThreadDispatch.Post`) and the worker gets a stand-in handle at once
  (`WPR.Backend.FNA.DeferredGpuHandles`: a one-byte native allocation, so it never equals a real
  FNA3D handle). **Uploads are posted too, with the data copied.** Posted work shares the FIFO
  with the waited-for calls, so a worker's own calls keep their order.
- **Every backend call that takes a texture or buffer** (samplers, vertex bindings, indexed draws,
  Set/Get data, disposal) runs the posted work at the head of the queue first when it is on the
  device thread (`DeviceThreadDispatch.DrainPosted`, which stops at the first waited-for call so a
  worker's draw is never mixed into the device thread's frame), then swaps a stand-in for its
  handle. Free when nothing was posted: one counter read.
- Render-target textures, effects, renderbuffers and readbacks are still waited for. A game whose
  loader spins the same way on one of those would still hang on desktop OpenGL.

Three turn-path bugs this exposed on OpenGL, all fixed. They come from one fact: on desktop
OpenGL a game's other threads reach the device through the device thread, so whatever the
device thread does mid-present, they meet.

- **A worker's waiting calls are held for the whole turned present**
  (`DeviceThreadDispatch.Suspended`, set in `WprBeginTurnedPresent`, cleared after the swap;
  posted resource work still runs). Drained mid-present they met the host backbuffer bound, and the
  device then re-bound the game's backbuffer over the target they had set. The Sims 3 builds each
  Sim's texture on a loader thread by drawing into a render target, and the Sim came out black (D4
  and D3 without DXT, where decompression slows the loader into that window); its 3D layer was also
  clipped into a square when queued draws landed on the host backbuffer.
- **A worker unbinding its target asks the device thread what "the backbuffer" is**
  (`WprBindBackbuffer` marshals itself), so the answer is taken when the call runs, not mid-present.
- **The game's viewport and scissor are restored after its backbuffer target is bound again**
  (`WprRebindAfterTurnedPresent`): FNA3D's GL driver flips a viewport against the backbuffer height
  when no target is bound, so one restored too early stayed shifted on the target, and every frame
  drawn without setting it again (the movie player's) landed offset.

Also seen: the off-screen `gamerun` window takes keyboard focus and SDL reads the global mouse, so
whoever uses the desktop plays the game. The local `Script.cs` now drops real keys and buttons.

### Android graphics: the driver is Vulkan, forced, on emulator and hardware alike (2026-09-07)

> **This section REVERSES the long-standing force of OpenGL.** If you are reading an older note,
> a commit message or a code comment that says Android forces OpenGL and that doing so is
> load-bearing against the T-pose, that is history — see "the T-pose was never the driver's fault"
> below. The reversal is only safe because `VertexFormatExpansion` exists; the two are a pair.

`FNA3D_PrepareWindowAttributes` walks `drivers[]` in order and takes the first whose
`PrepareWindowAttributes` succeeds. The compiled-in set differs per platform:

| head | drivers available | what it gets |
| --- | --- | --- |
| Windows | D3D11, OpenGL | **D3D11**, picked automatically — the head declares no driver at all |
| Android | OpenGL, Vulkan | **Vulkan**, forced by name — automatic order offers OpenGL first |

Android forces it in two places, deliberately: `fna3d.env` sets `FNA3D_FORCE_DRIVER=Vulkan`
process-wide, and `AndroidPlatform` declares `caps.GraphicsDriver(GraphicsDriver.Vulkan, …)`. The
env file covers the process from the first instruction; the capability says it in the one file
you are meant to read to learn what this platform is.

**Why Vulkan, in the order the reasons actually matter:**

1. **The OpenGL driver blocks any off-thread GPU call until the next swap.** `ForceToMainThread`
   in `FNA3D_Driver_OpenGL.c` — 19 call sites, the `CreateTexture*` / `SetTextureData*` /
   `Gen*Buffer` / `Set*BufferData` / `CreateEffect` set — appends the call to a command list and
   parks the caller on a semaphore, drained only by `ExecuteCommands` from `OPENGL_SwapBuffers`.
   A game loading content on a worker while its game thread waits on a lock that worker holds
   deadlocks outright: no crash, no CPU, nothing in the log. **Fable: Coin Golf** did exactly
   that, stopping for ever on asset 33 of 602; on Vulkan it loads all 602. Vulkan and D3D11 have
   no such queue — that entire bug class does not exist off the GL driver. (The `OffThreadGpuCalls`
   mitigation below still exists and still helps, but it only makes the wait finite, not absent.)
2. **Emulator and hardware then run the same driver**, so an emulator result means something. The
   old arrangement ran GL on phones and Vulkan on the emulator, which made every emulator
   observation unusable as evidence about a device.

**The T-pose was never the driver's fault**, and this is the correction that unblocked everything
above. Mirror's Edge drew its world, lighting and reflections perfectly while every skinned
character stood in bind pose, and that was blamed on FNA3D's unfinished Vulkan driver
mistranslating `SkinnedEffect`'s relative-addressed `float4x3 Bones[72]`. Measured 2026-09-07, it
does not:

- the SPIR-V is **correct** — verified instruction by instruction for the one-bone and four-bone
  variants, including the relative addressing, the x3 bone stride and the array bounds;
- the bone matrices **arrive correctly**, and animate;
- the real cause is **vertex formats**. `BlendIndices` is `Byte4`, which FNA3D maps to
  `VK_FORMAT_R8G8B8A8_USCALED`. The `*_SCALED` formats are **optional** in Vulkan for vertex
  buffers and Adreno does not implement them, so the attribute delivered nothing, every vertex
  read bone 0, bone 0 is identity, and the mesh rendered its bind pose. `Short2` and `Short4` are
  unsupported for the same reason. Measured on an Adreno 750, logged as `[wpr-vfmt]`.

The fix sits **above the driver**: `Microsoft.Xna.Framework.Graphics.VertexFormatExpansion`
rewrites those three formats to float and converts the data to match. The game still sees the
`VertexDeclaration` it created; only `GraphicsDevice.PrepareVertexBindingArray` is shown the
translated one. **Revert that and you must revert the driver force too** — the comment in
`fna3d.env` says so, keep it saying so.

**It ASKS THE DEVICE, per format, and rewrites only what the device refuses (2026-09-20).** It ran
unconditionally from 2026-09-07 until then, which silently rewrote the vertex layout of every
affected game on **the desktop head too** — for a bug no desktop GPU has. That was justified at the
time on the grounds that a real query "would need a new FNA3D vtable entry, a P/Invoke and a seam
member". It needs none of those: **`FNA3D_GetSysRendererEXT` already hands back the driver's own
`VkInstance`/`VkPhysicalDevice`**, and it is exported by every binary shipped here (verified with
`llvm-nm` on all three `libFNA3D.so` and `llvm-objdump` on `FNA3D.dll`), so
`WPR.Backend.FNA.VulkanVertexFormatSupport` asks `vkGetPhysicalDeviceFormatProperties` for
`VK_FORMAT_FEATURE_VERTEX_BUFFER_BIT` over handles it is given. **No native rebuild** — which
matters, because the vendored C is not compiled by any build here.

Four things that are load-bearing:

- **`FNA3D_GetSysRendererEXT` returns WITHOUT WRITING A BYTE unless `sysrenderer->version` matches**,
  and reports nothing when it does so. The struct is passed `ref` with `version` set, and
  `rendererType` is pre-set to a sentinel so an ignored call is detectable. Skip that and an ignored
  call reads as `rendererType == 0` — OpenGL — which answers "all formats fine" and puts the T-pose
  straight back on an Adreno. `FNA3D_SYSRENDERER_VERSION_EXT` is currently **0**, so a zeroed struct
  happens to pass today; do not rely on that.
- **Every failure answers "unsupported", i.e. expand.** A missing export, a null handle, an
  unresolvable proc — none of it proves the format works, and expanding is lossless. Being wrong
  that way costs a wider vertex buffer; being wrong the other way is a character in bind pose.
- **Ask the device FNA3D chose, never one of our own.** Creating a throwaway `VkInstance` to ask is
  the obvious alternative and answers for whichever physical device *we* picked, which on a
  multi-GPU machine need not be the one rendering.
- **Per format, not per device.** The device answers three separate questions and only the refused
  ones are rewritten.

Read it out of the log — one line per launch, including the "nothing to do" case, because "did not
run" and "ran and did nothing" are otherwise indistinguishable from inside a game. Measured on a
Galaxy S24 (Adreno 750), which is the reference device for the original defect:

```
[wpr-vfmt] device vertex formats — Byte4=UNSUPPORTED Short2=UNSUPPORTED Short4=UNSUPPORTED (queried); expansion ENABLED
```

**Scope, measured 2026-09-20: three of the 36 installed titles construct one of these elements at
all** — Mirror's Edge, Kinectimals and citizen12 (`VertexElement` ctor sites with a format constant
of 5/6/7). For every other game `TryTranslate` returns null and none of it executes on any driver.
**So this pass is never the explanation for a game that has no such element** — dump the game's
`VertexDeclaration` before suspecting it. Note the sweep reads declarations built in game IL;
content-supplied ones (`Model` XNBs) would need XNB parsing to enumerate.

**Two FNA3D Vulkan fixes were needed to make the switch, and `libFNA3D.so` was rebuilt for all
three ABIs to carry them.** The vendored C under `FNA.Platform/lib` is still not compiled by any
build here, so the `.so` and the `.c` are kept in step by hand:

- **`VK_ERROR_SURFACE_LOST_KHR` is handled alongside `OUT_OF_DATE`.** On Android it is the normal
  case, not an exotic one: backgrounding destroys the `ANativeWindow` and takes the `VkSurfaceKHR`
  with it. Without this the swapchain is never torn down, `validSwapchainExists` stays 1, and
  every present after a resume fails identically — **the game runs on with a black screen and
  nothing in the log**. Recreating the swapchain is sufficient only because
  `VULKAN_INTERNAL_DestroySwapchain` destroys the surface too; if that stops being true this needs
  its own path.
- **`preTransform` asks for `IDENTITY`, not `currentTransform`.** `currentTransform` is a *promise*
  that the application has already rotated its rendering to match the display, and FNA3D has not —
  it renders axis-aligned to the swapchain extent. On Android a landscape activity over a
  portrait-native panel reports `ROTATE_90`, so honouring it presents the image **sideways while
  touch, which never goes through the swapchain, stays correct**. Desktop surfaces report
  `IDENTITY`, so this changes nothing there. The cost is a compositor rotation pass on some mobile
  GPUs; correctness first.
- **…and so `VK_SUBOPTIMAL_KHR` from that mismatch is ignored (2026-10-23).** Android answers EVERY
  present on such a swapchain with `VK_SUBOPTIMAL_KHR`, and FNA3D rebuilt the swapchain on it, so
  every landscape game rebuilt its swapchain on every frame (the log showed one "Opaque
  presentation unsupported!" per frame; that warning is printed by each swapchain build). On the
  `WPR_Verify` emulator's gfxstream Vulkan this froze the whole VM about a second after the first
  frame (qemu at 0% CPU, adb offline; PvZ and Galactic Reign; portrait Montezuma was fine). On a
  phone it was a hidden per-frame cost. `VulkanSwapchainData` now records the surface's
  `currentTransform` and `currentExtent` when the swapchain is built, and a suboptimal present is
  ignored while both are unchanged; a real rotation or resize still rebuilds, and so does a lost
  surface. Log: `Vulkan: present is suboptimal only because the swapchain is not pre-rotated
  (surface transform 0x2); keeping it` once per swapchain, and `Vulkan: recreating the swapchain
  (acquire …, present …)` for every rebuild from the present path. `libFNA3D.so` rebuilt for all
  three ABIs (the unmodified source rebuilds to identical code, only debug paths differ; swizzle
  check passes). The Windows `FNA3D.dll` was not rebuilt and needs no change: desktop surfaces
  report `IDENTITY`. Verified on `WPR_Verify` (A2): PvZ menu → level 1-1 → planted, home and back
  (one rebuild for the lost surface, then kept), Galactic Reign and Montezuma. Not run on the S24
  or a Mali device.

**Things that did not change with the switch:**

- **It must be a real process environment variable.** FNA3D reads the hint through `SDL_GetHint`,
  which falls back to `SDL_getenv`; .NET's `Environment.SetEnvironmentVariable` does **not**
  propagate to the native environ on Unix, so FNA's own `gldevice` launch-parameter path
  (`FNAPlatform.cs:54`) cannot set it on Android.
- **Forcing is a hard selection, not a preference.** The loop `continue`s past every driver whose
  `Name` doesn't `strcmp`-match. The name is exactly `"Vulkan"` (`FNA3D_Driver_Vulkan.c`) —
  `"vulkan"` and `"VK"` silently match nothing.
- **A declined driver falls through by name, it does not throw.**
  `SDL2_FNAPlatform.PrepareWindowAttributesWithFallback` walks an explicit ladder — the requested
  driver first, then `OpenGL`, `Vulkan`, then automatic — logging each attempt. **Whatever was
  requested stays first in the ladder, including "automatic"**, or the fallback silently overrules
  the head's declaration.
- The lever is `WPR.Backend.FNA.GraphicsDriverSelection.Apply(name)` —
  `SDL_SetHintWithPriority(..., SDL_HINT_OVERRIDE)`, the one thing that beats the process env var.
  Pass **null**, never `""`, to restore automatic order: an empty string is still non-NULL to
  `SDL_GetHint` and would `strcmp` against every driver name and match none.
- **`GraphicsDriver.Unspecified` ≠ `Automatic`.** `Unspecified` leaves the lever untouched (what
  Windows wants); `Automatic` actively *clears* a force. On Android `Automatic` would mean OpenGL,
  because that is what FNA3D offers first — which is why the head names Vulkan explicitly rather
  than declaring `Automatic`.
- No rebuild needed to re-test a driver: an `fna3d_driver.txt` next to the app's external files
  (containing `OpenGL`, `Vulkan` or `auto`) overrides the declaration. This is the first thing to
  reach for on a phone whose Vulkan driver misbehaves.

  ```powershell
  adb shell "echo OpenGL > /storage/emulated/0/Android/data/com.wpr.android/files/fna3d_driver.txt"
  ```

**What went away with the switch.** `Graphics/GraphicsDriverPolicy.cs` is deleted — there is no
per-device branch any more, so nothing asks `AndroidDeviceKind.IsEmulator()` about graphics. The
old rule "detection is biased to false negatives, never invert it" is therefore moot, and the old
observation that the emulator cannot render the OpenGL path (it leaves the game's clear colour on
screen — a flat white for Mirror's Edge — with no error) no longer matters because nothing runs
that path by default. If you ever reintroduce a per-device branch, you are reintroducing the
problem in reason 2 above.

That last observation also no longer reproduces: with the settings picker below set to opengl,
Zuma's Revenge renders correctly on the `Pixel_Dev` emulator (API 36, `OpenGL Renderer: Android
Emulator OpenGL ES Translator`), measured 2026-09-16. Something between the two dates fixed it, so
do not plan around "the emulator draws nothing on GL" — verify it per game instead.

One surviving GL artefact worth knowing, because it is a `SIGSEGV` rather than a wrong pixel:
`OPENGL_GetVertexBufferData` / `OPENGL_GetIndexBufferData` are implemented with
`glGetBufferSubData`, declared `GL_PROC(NonES3, …)` and therefore **never resolved under OpenGL
ES** — the pointer stays NULL, the only guard is an `SDL_assert` compiled out of our prebuilt
release `.so`, and the first `GetData` branches to address 0. `GpuBufferShadow` serves those reads
from a CPU-side mirror instead, so the crash cannot happen on either driver. (Its doc comment
still says "where `fna3d.env` forces the OpenGL driver" — stale wording, live code.)

### The forced Vulkan has a user-reachable escape hatch (2026-09-16)

`Configuration.GraphicsDriver` — **null by default**, meaning "whatever the platform declares", so
an untouched install is byte-for-byte the behaviour above. Surfaced on the **Android settings
page** as a two-button GRAPHICS picker (vulkan / opengl); `AndroidPlatform.ChosenGraphicsDriver()`
reads it and declares that instead of a hardcoded `GraphicsDriver.Vulkan`. Read as a **fact about
this device**, exactly like `fna3d_driver.txt`, not as a preference — the note under the picker
tells the user to leave it alone.

**Why it had to exist.** `SDL2_FNAPlatform.PrepareWindowAttributesWithFallback` can only rescue a
driver that declines at `FNA3D_PrepareWindowAttributes`. One that *prepares* and then fails inside
`FNA3D_CreateDevice` — or natively below it — reaches the player as a black screen, an error
dialog and a dead game process, **every launch, with no way back**, and FNA3D's Vulkan driver is
still unfinished. The triage tool for that was `fna3d_driver.txt` in the app's external files dir,
which needs a PC and adb; someone holding only the phone had nothing. Prompted by issue #39 (Zuma's
Revenge on a Realme Note 60x — Unisoc T612 / Mali-G57), which reproduces on **no** configuration
available here: Windows D3D11, Windows Vulkan, Windows Vulkan with DXT decompression forced, the
emulator on gfxstream Vulkan and the emulator on SwiftShader Vulkan all play it end to end,
including level 1. A Mali Vulkan driver is the one variable none of those covers.

Four things about the shape:

- **Vulkan is stored as `null`, not as the string.** "Never touched" and "set back to the default"
  are then the same bytes, and a future change of platform default is not silently pinned by an old
  config.json. Same reasoning as `TiltSimulationEnabled`.
- **An unrecognised name falls back to Vulkan rather than being passed through.** FNA3D `strcmp`s
  the force hint against a driver's own `Name`, so a typo would match nothing and *become* a launch
  failure — the exact thing this exists to get someone out of. `fna3d_driver.txt` stays the
  unvalidated escape hatch, and still wins over this (it is resolved inside
  `GraphicsDriverPreference.ResolveDriverName`).
- **Deliberately no "automatic" option.** On Android automatic means OpenGL, because that is what
  FNA3D offers first, so it would be a third name for one of the two buttons.
- **It takes effect from the next launch**, because `GameActivity.OnDestroy` kills the `:game`
  process and every launch re-reads config.json in a fresh one — the same property the vibration
  switch relies on. **The launcher process is a different matter** — see the section below, which
  is where picking a driver stopped being a thing you had to restart WPR for.

**The error dialog now leads with the driver.** `GameLauncher.HandleGameResult` prepends
`graphics driver: <name> — if this game never starts on this device, try the other one under
settings → graphics.` — first, because a managed failure dumps a stack trace long enough that
anything appended after it is never read, and unconditionally, because the worst case carries no
exception to match on ("the game process exited unexpectedly"). It says nothing when the process
composed no platform, which is what Android recreating straight into `GameShortcutActivity` does.

**Not built for Windows.** The setting is cross-platform but the desktop head declares no driver at
all (D3D11 is picked automatically), so there is nothing for it to override there.

No `ApplicationPatcher.Version` bump and no reinstall. **No manifest change either**, so a rebuild
is enough — unlike the shortcut feature.

### The graphics picker needed the LAUNCHER to recompose, not the game (2026-09-19)

Reported as "changing the graphics setting does nothing until I restart WPR". Measured on a Galaxy
S24, and the report is half right in a way that is worth keeping, because the obvious reading of it
sends you to the wrong process:

- **The game was always correct, immediately.** Pick `opengl`, launch a game without restarting
  anything, and the `:game` process comes up `driver=OpenGL` → `FNA3D driver: forced to 'OpenGL'`
  → `FNA3D Driver: OpenGL`. It is born fresh for every launch (`OnDestroy` kills it — verified, the
  pid is gone after Back) and re-reads config.json, so it cannot be stale.
- **The LAUNCHER process was stale for its whole lifetime.** After the tap there were **zero**
  `[wpr-platform]` lines: nothing recomposed. `SettingsActivity` wrote config.json and repainted
  the two buttons, and that was all.

**The driver is the one setting that is read once and STORED rather than consulted live.**
`AndroidPlatform.ChosenGraphicsDriver()` reads config.json during `ServicesSetup.Start()`,
`PlatformComposition` hands the answer to `GraphicsDriverPreference`, and that holds it until the
process dies. Compare `VibrationBackend.IsEnabled`, which is
`Configuration.Current?.VibrationEnabled != false` evaluated on every read and therefore never goes
stale. **Prefer the live shape for a new setting**; storing one costs you the call below.

**The tell is a game's info page contradicting itself on one screen** — this is the cheapest way to
recognise a repeat, and all three rows come from different places:

```
SELECTION   driver=Vulkan                                 <- GraphicsDriverPreference, launcher, stale
PROBE       last launch ran on OpenGL (asked for OpenGL)  <- breadcrumb file, written by :game
BACKEND     OpenGL                                        <- capabilities file, written by :game
```

**The half that actually mattered is not the info page.** `GameLauncher.HandleGameResult` prepends
`graphics driver: <name> — … try the other one under settings → graphics`, and it read the same
stale static. So the one message that has to be right — shown to someone whose game just died,
telling them which driver to move off — kept naming the driver they had already moved off.

**The fix is one call: `ServicesSetup.Start()` at the end of
`SettingsActivity.SelectGraphicsDriver`.** Three things about it:

- **`ServicesSetup.Start()`, not a `PlatformComposition.Apply` of our own.** Building the descriptor
  here would be a second copy of the composition root's argument list to keep in step.
- **Running it twice in one process is safe by construction**, which is the same property
  `GameActivity`'s `:game` process already relies on. The two registries that do something on
  replace are fine in the launcher, where no game runs: `SensorBackend.SetAccelerometer` calls
  `ResetForNewLaunch()` on the displaced provider (nothing is subscribed) and
  `VibrationBackend.SetDevice` stops it (nothing is buzzing). Both comments already anticipate a
  head re-composing.
- **Order is load-bearing:** `Save()` → `GraphicsDriverProbe.Clear()` → recompose. Clearing after
  the recompose would leave a verdict the fresh declaration had already read, and picking Vulkan
  back (which stores **null**) would immediately re-demote to OpenGL.

Verified on a clean API 36 AVD, watching one PID across the whole test: startup `driver=Vulkan`;
tap opengl → `driver=OpenGL` with config `"OpenGL"`; tap vulkan → `driver=Vulkan` with config
`null`, no re-demote. Before the fix the same sequence produced no line at all.

**Two notes for anyone verifying this on hardware.** A build from this repo **cannot be installed
over the build on the S24** — different signing key (`INSTALL_FAILED_UPDATE_INCOMPATIBLE`, behind a
version-code error that surfaces first), and uninstalling would take
`/Android/data/com.wpr.android/` with it, i.e. every installed game and config.json. And a second
AVD is one command (`avdmanager create avd -n <name> -k "system-images;android-36;google_apis;x86_64"`),
which is the way to test without disturbing whatever is already running on `Pixel_Dev`.

No `ApplicationPatcher.Version` bump, no reinstall, no manifest change — a rebuild is enough.

### The driver choice learns from a crash, across process deaths (2026-09-18)

`GraphicsDriverProbe` (`Src/Engine/WPR.Engine.Graphics/`) remembers whether the last launch that
asked for a driver ever got a frame onto the screen, and demotes Vulkan to OpenGL when it did not.
It closes the gap the Settings picker above leaves: that picker requires the player to *discover*
it, on a device where every launch dies.

**It is a crash-loop breaker, not a capability cache, and the distinction decides the design.** The
obvious shape — probe what the device supports, cache the answer — cannot work, because the
failures worth defending against do not report themselves:

- a driver that declines at `FNA3D_PrepareWindowAttributes` needs none of this; the ladder in
  `PrepareWindowAttributesWithFallback` already tries the next one;
- a driver that *prepares* and then fails inside `FNA3D_CreateDevice` arrives as a managed throw
  (`FNALoggerEXT.FNA3DLogError` → `InvalidOperationException`) through native frames that are now
  half-initialised, so retrying in-process is unsafe;
- and the common case on a bad mobile driver is **SIGSEGV**, which can be neither caught nor logged.

Nothing can be learned inside such a launch. It can only be learned across one — hence a file.

**The three hook points, and why each is where it is:**

| stage | site |
| --- | --- |
| decide | `AndroidPlatform.ChosenGraphicsDriver()` — "this device cannot do Vulkan" is a fact about the device, which is what a `PlatformDescriptor` states |
| mark attempting | `FnaPlatformBackend.CreateWindow`, first call (FNA3D's first contact with the driver); `FnaGameHost` passes it the requested driver |
| mark working | `FnaGraphicsBackend.SwapBuffers`, first call only |

All three already sat in assemblies referencing `WPR.Engine.Graphics`, so this needed **no new
project references**.

**The pending window is device creation plus one frame, and that is what makes a single strike
safe.** Marking happens just before anything touches the driver — `PrepareWindowAttributes` is the
first call in and it builds a throwaway device to test the waters, so a hostile driver can take the
process down well before `CreateDevice`. It is retired at the first presented frame. For a user to
be mistaken for a crash they would have to force-close inside that window. Measured on a real
launch: pending on disk at **0.93 s**, before the first frame, surviving a hard `Stop-Process`.
If a device ever argues otherwise the knob is N consecutive pendings, not a wider window.

**Ordering is load-bearing:**

```
fna3d_driver.txt  >  Configuration.GraphicsDriver (explicit)  >  breadcrumb  >  platform default
```

An explicit choice must outrank the probe. The probe exists so someone holding only a phone need
not discover the setting; the setting exists so someone the probe got *wrong* can overrule it.
Letting the probe win collapses the second case into the first. For the same reason
`SettingsActivity.SelectGraphicsDriver` calls `GraphicsDriverProbe.Clear()` — picking Vulkan back
stores **null**, so a surviving verdict would immediately re-demote and the button would look broken.

Four more things that are deliberate:

- **Only Vulkan is demoted.** A pending mark against OpenGL means the fallback itself failed, and
  returning to Vulkan on that evidence would flip the device between two broken drivers for ever.
- **Keyed on `Build.FINGERPRINT`**, which changes with any system image update — a driver condemned
  by firmware that is no longer installed gets another chance.
- **Not in `config.json`.** `Configuration.Save()` serialises the whole object, so the launcher
  holding a copy loaded before `:game` wrote would silently clobber it on its next unrelated save.
  Its own line-based file beside `fna3d_driver.txt`, readable with `adb shell cat`.
- **An unparseable file reads as "no verdict".** The worst a torn write can do is let the preferred
  driver be tried again; this file may cost a device its first choice, never its only one.
- **The mark goes down at the first window creation, not at the start of the launch
  (2026-10-04).** `ApplicationLaunch.Start` refused a Silverlight UI app (until Stage C,
  2026-10-13) or an unsupported runtime type before any `Game` exists, so such a launch never touches the driver and never presents a frame.
  Marked at the top of `FnaGameHost.RunAsync`, it left a pending mark, and once strikes became
  sticky one tap on an unsupported title condemned Vulkan for good. Seen on the S24 the day 0.2.03
  was built: every later game ran on OpenGL, where Doodle God then crashed. Anything new that can
  end a launch must either come after `CreateWindow` or not care about the probe.

**What it cannot see**, and this is not a gap to be closed by tuning it: a driver that initialises,
presents, and dies a minute later is marked working at frame one and never demoted. **3D Brick
Breaker Revolution is exactly that case** — see the descriptor-pool section below. The Settings
picker remains the answer there; the two mechanisms are complementary and neither replaces the other.

**A strike is sticky (2026-10-04, issue #51).** The first version stored only the LAST launch, so the
demotion undid itself: Vulkan dies pending → next launch OpenGL → its first frame writes `ok` over
the mark → the launch after is Vulkan again. Every other launch crashed. A driver that leaves a
pending mark is now written as `condemned=<driver>` and carried through every later write until that
driver itself presents a frame, `Clear()` (the Settings picker), or the device key changes. The key
is now `Build.FINGERPRINT + "|wpr " + versionName`, so a WPR update — which may carry the fix —
retries Vulkan. Recognise a repeat in a logging-run report by `driver=` alternating between runs.

**FNA3D's Vulkan driver now asks before enabling optional features (2026-10-04, issue #51).**
`VULKAN_INTERNAL_CreateLogicalDevice` turned on `occlusionQueryPrecise`, `fillModeNonSolid` and
`samplerAnisotropy` unconditionally; a Mali-G57-class phone (realme RMX3938) lacks one, so
`vkCreateDevice` returned `VK_ERROR_FEATURE_NOT_PRESENT` and **no** game could create a device. Each
is now enabled only when `vkGetPhysicalDeviceFeatures` reports it, with a fallback at its one use
site (wireframe → solid fill, anisotropic → linear, precise → imprecise query), and FNA3D logs
`Vulkan device lacks optional features: …` once. `libFNA3D.so` rebuilt for all three ABIs (swizzle
check passes); **the Windows `FNA3D.dll` was not**, which only matters for forced Vulkan on desktop.
Not run on a Mali device yet: no device here lacks these features.

**FNA3D's GL driver gives worker threads their own shared contexts on Android (2026-10-04).**
Bejeweled Live+ deadlocked on OpenGL: its `ResourceManager.LoadingProc` thread holds the manager's
lock while `ContentManager.Load` → `CreateTexture2D` waits in `ForceToMainThread`'s queue, and
`GameMain.Update` → `ResourceManager.LoadImage` blocks on that lock, so no swap ever drains the queue
(the "game thread blocks on the worker" case under "A suppressed draw starves FNA3D's off-thread
command queue"). Now `ForceToMainThread` first tries a pool of **two** contexts created at device
creation with `SDL_GL_SHARE_WITH_CURRENT_CONTEXT`: the worker borrows one, runs the same command via
`FNA3D_ExecuteCommand`, `glFinish()`es and releases it. Only texture create/upload (2D, 3D, cube)
and vertex/index buffer create/upload go there; effects (MojoShader is not thread-safe), readbacks
(FBOs are not shared) and renderbuffers keep the queue. Things that are load-bearing:

- **Surfaceless `eglMakeCurrent`, called directly through `SDL_LoadObject("libEGL.so")`.** EGL lets a
  window surface be current on one thread only, and SDL's Android `GLES_MakeCurrent` turns
  `(NULL window, ctx)` into `(NULL, NULL)`, so `SDL_GL_MakeCurrent` cannot do it. Needs
  `EGL_KHR_surfaceless_context`; if the call fails the pool disables itself and the queue carries on.
- **A thread-local flag (`OPENGL_INTERNAL_OnWorkerContext`) does two jobs**: it lets the re-entered
  function past its `threadID` check, and it makes `BindTexture`/`BindVertexBuffer`/`BindIndexBuffer`
  bind raw instead of touching the renderer's binding cache, which describes the DEVICE context.
- **`__ANDROID__` only.** Desktop would need WGL/GLX `MakeCurrent` instead, and `FNA3D.dll` cannot be
  rebuilt here. `FNA3D_OPENGL_WORKER_CONTEXTS=0` disables it, but it must reach the native environ:
  `setprop debug.mono.env` did NOT (the pool still came up), so an A/B needs a rebuilt `.so`.
- **Known gap:** a worker updating a texture the device context already has bound may not be seen
  until it is re-bound (GL's cross-context visibility rule). Loading creates new textures, so this
  has not shown up.

Verified on the `WPR_Verify` emulator (`OpenGL ES 3.1` translator), Release APK, OpenGL forced:
log line `OpenGL worker contexts: 2`, Bejeweled Live+ menu → Classic → live board → a scored match.
A/B with the pool off: Bejeweled black (hung). The Treasures of Montezuma (clear colour only) and
Galactic Reign (black) look **identical with the pool on and off** on that emulator's GL, so their GL
problem is separate and pre-existing. One run of Galactic Reign after Montezuma reported a crash
that five repeats, including that exact sequence, did not reproduce.

**Update, same day: that "separate problem" was FNA3D trusting stub entry points on ES (issue #62).**
`GL_PROC_EXT` marks a feature supported when `SDL_GL_GetProcAddress` returns non-NULL, but Android's
`eglGetProcAddress` may return a stub for an unimplemented function — the emulator's logs
`E libEGL: called unimplemented OpenGL ES API` and does nothing. On the emulator (ES **3.1**) that
made `supports_ARB_draw_elements_base_vertex` true, so every indexed draw with a base vertex drew
nothing: Archer black, Montezuma clear-colour only, Galactic Reign black. `OPENGL_INTERNAL_VerifyESCapabilities`
(called in `LoadEntryPoints` when `useES3`) now requires ES 3.2 or `GL_OES/EXT_draw_elements_base_vertex`
for base-vertex draws (the attribute-offset fallback already existed) and forces `supports_NonES3`
/ `supports_NonES3NonCore` to 0. `glColorMaski` (ES 3.2 too) was deliberately left alone: it is
called behind an `SDL_assert` only, so a stub is safer than NULL. **The recogniser: the stub message
roughly once a second, from the render thread, on a game that shows nothing on GL** — and a native
`debuggerd -b` showing the render thread in `OPENGL_SwapBuffers` → `dequeueBuffer` is a symptom of
that, not a cause. Verified: Archer title screen and Montezuma main menu on emulator GL, zero stub
messages, Bejeweled still plays. **Galactic Reign's crash is unrelated**: a stack overflow in its own
`ScreenManager.DoNavigation` → `DispatchHelper.Dispatch` → our inline `Dispatcher.BeginInvoke` loop,
seen on either driver, ~2 in 9 launches.

### Capabilities, not driver names (2026-09-18)

`IGraphicsCapabilities` + `GraphicsCapabilities` + `GraphicsCapabilitiesStore`
(`Src/Engine/WPR.Engine.Graphics/`). Measured in `FnaGraphicsBackend.PublishCapabilities` at the
first presented frame — the earliest point where a device exists *and* has demonstrably worked —
and read by crash reports (`GraphicsBackend`). It used to be shown as the `graphics` section of the per-game info screen (`GameInfoActivity` / `GameInfoDialog`), which was removed on 2026-09-29 along with `WPR.Loader.GameDiagnostics`; a logging run's report is how those facts reach anyone now.

**Why this can live in the engine tier when `IGraphicsBackend` cannot.** The RHI seam speaks
`Texture2D` and `GraphicsDevice`, game-facing identities the patcher rescopes into
`WPR.Framework.Xna`, which also consumes the seam — so a contract naming them is un-invertible.
Everything here is an `int`, a `bool` or a `string`, the same exemption `Audio3DParams` used to
escape to `System.Numerics.Vector3`. **Keep it that way**: the moment a member names an XNA type,
this has to move back. That is also why the *resource* half of an abstract graphics layer
(`ITexture`/`IRenderTarget`/`IShader`) was deliberately **not** built — `IGraphicsBackend` already
is that seam, and a second neutral vocabulary would mean translation on the per-frame path.

**This is the DEVICE model and it is NOT `ProfileCapabilities`.** That type (in
`WPR.Framework.Xna/Graphics/`) is XNA 4.0's own capability model and is **hardcoded per
`GraphicsProfile`** — Reach guarantees `MaxTextureSize = 2048`, 16 samplers, shader model 2.0. It
describes the contract a WP7 title was compiled against and validates what the game asks for. This
describes the hardware underneath. Do not cross-wire them: a diagnostic screen that mixed the two
would claim a compatibility verdict it never computed.

**`SupportsOffThreadResourceCreation` is the capability with a real bug behind it**, and it is the
reason this layer is worth having at all. Vulkan and D3D11 true, OpenGL false — because
`ForceToMainThread` exists on 19 entry points in `FNA3D_Driver_OpenGL.c` and has no counterpart in
the others. That single flag is Need for Speed: Undercover, Fable: Coin Golf and Game Room:
Pitfall!, and it is why "which driver" is the wrong question. It is `bool?`, and **null means we
were not told** (automatic selection): defaulting that to true would be right on the desktop and
wrong as a claim. `SDL2_FNAPlatform.SelectedDriverName` publishes the ladder's winner so the record
names the driver that actually ran, not the one requested — a declined driver falls through
silently, and a verdict naming only the request would hide it.

**Only measurable facts are reported.** FNA3D's entire capability surface is **eight** functions
(`SupportsDXT1` / `SupportsS3TC` / `SupportsBC7` / `SupportsHardwareInstancing` /
`SupportsNoOverwrite` / `SupportsSRGBRenderTargets` / `GetMaxTextureSlots` /
`GetMaxMultiSampleCount`). GL/GLES version, maximum texture size and multiple-render-target support
are **absent rather than guessed** — adding them needs new FNA3D entry points. A row that is
secretly a constant is worse than a missing row, and that is the rule to keep when extending this.

Measured on a Galaxy S24 Ultra (Adreno 750, Android 16) and on the desktop, same game, both drivers
— the three that differ are what makes this real rather than decorative:

| | OpenGL | Vulkan |
| --- | --- | --- |
| off-thread loading | no | **yes** |
| no-overwrite locks | no | **yes** |
| sRGB render targets | no | **yes** |

Both this and the probe write beside `fna3d_driver.txt`; `Configure` is called from
`AndroidPlatform.Describe`, and a platform that never calls it (Windows, which declares no driver)
gets the usual absent-means-unavailable degradation with no file written.

No `ApplicationPatcher.Version` bump, no reinstall, no manifest change.

### Vulkan swapped red and blue on every `Bgr565` texture (2026-09-18)

`XNAToVK_SurfaceSwizzle[Bgr565]` in FNA3D's Vulkan driver was `{B, G, R, ONE}`, applied at every
`VULKAN_INTERNAL_CreateTexture`. It is now `IDENTITY_SWIZZLE`. **This shipped in all four prebuilt
binaries**, so it was live on every Android device from the day the driver was forced to Vulkan
(2026-09-07) — and on Windows only for someone reproducing Android with
`FNA3D_FORCE_DRIVER=Vulkan`, since D3D11 and OpenGL both map the format correctly.

**The swizzle was the bug, not the correction.** Vulkan names the components of a *packed* format
MSB-to-LSB while XNA and DXGI name them LSB-to-MSB, so `VK_FORMAT_R5G6B5_UNORM_PACK16` (R in bits
11..15, G in 5..10, B in 0..4) is bit-for-bit the same layout as XNA's `Bgr565` and as
`DXGI_FORMAT_B5G6R5_UNORM`. Identity is right; a swizzle introduces the very swap it looks like it
is undoing. The neighbouring `Bgra5551 -> VK_FORMAT_A1R5G5B5_UNORM_PACK16` is identity for exactly
the same reason, and is correct — **that entry is the tell if you ever doubt this one.**

**The symptom is a blue character on an otherwise perfect screen, which is why it survived.** Only
the affected textures move, and greys do not move at all (R=G=B is invariant under an R/B swap), so
nothing looks "broken" — one asset looks recoloured. **Brain Challenge HD** is the reference case:
its 289 XNB textures are all `SurfaceFormat.Color`, and of its own `.gtx`/`.bbm` container assets
exactly **two** — `man_0.gtx` and `woman_0.gtx`, the coach sprite sheets — carry format code 8,
which its loader maps to `Bgr565`. So the scientist coach rendered with blue skin while his white
lab coat stayed white. Decode the sheet both ways and the reported symptom is reproduced exactly;
that is the cheapest confirmation and it needs no device.

**Expect this to have recoloured more than one game.** Nothing about it is Brain Challenge
specific — any WP7 title with a 16-bit texture was affected on Android. Worth re-checking anything
whose art was ever reported as "wrong colour" or "tinted".

**How to re-verify without a game.** A ~90-line console harness against the prebuilt DLLs uploads
known texels and reads the backbuffer back, which turns the whole question into three numbers
(see "Launching one game without the launcher UI" for the harness recipe; register the backends
`FnaGameHost.RunAsync` registers, then `SetData` a `Bgr565` texture and `GetBackBufferData`).
Measured on an RTX 5090, forced Vulkan:

```
before   texel 0xF800 (red) -> screen R=0   G=0   B=255     0x001F (blue) -> R=255 G=0 B=0
after    texel 0xF800 (red) -> screen R=255 G=0   B=0       0x001F (blue) -> R=0   G=0 B=255
```

**The fix is a 16-byte edit to a static const array in each binary, plus the vendored `.c`.** The
`.c` under `FNA.Platform/lib` is still not compiled by any build here, so source and binary are
kept in step by hand — the `.c` change is what stops a future rebuild silently reintroducing this.
`scratchpad/patch-swizzle.ps1` is the tool: it locates the table by anchoring on entries 12..17 (a
96-byte run of non-zero swizzles, unique in the image) rather than a hardcoded offset, verifies the
23 entries it does not own against the vendored source, refuses to write unless exactly one table
is found, and is idempotent. Patched files: the three `Libraries/<abi>/libFNA3D.so`,
`Src/Platforms/WPR.Platform.Windows/FNA3D.dll`, and the build-output copies. **Sizes are unchanged
and nothing outside the table moved** — verify any repeat with a byte diff, not a green build.

**A second finding, and it is only half fixed.** The shipped x64 `FNA3D.dll` already carried
`Bgra4444 = {G, R, A, B}` while the vendored `.c` and all three Android `.so` said `IDENTITY` —
i.e. the libs had drifted, and Android had a `Bgra4444` defect Windows did not. `{G,R,A,B}` is
independently the right answer (`VK_FORMAT_B4G4R4A4_UNORM_PACK16` decodes XNA's A/R/G/B nibbles as
B/G/R/A, so the hardware's R is the real G, its G the real R, its B the real A, its A the real B),
so Android and the `.c` were brought into line with it. **It was unverified at runtime**, because
`Bgra4444` rendered solid black in the harness on *both* Vulkan and OpenGL, before and after the
change alike.

> **Both halves of that are now settled (2026-09-21).** The black was **managed**, not a driver
> problem: an `//Experimental! RnD / TEMP` block in `Texture2D`'s constructor rewrote `Bgra4444` to
> `Color` without converting a pixel, so `SetData`'s `requiredBytes` check measured the game's
> 2 bytes-per-pixel data against `Color`'s 4 and uploaded **nothing** — see the GLES texture
> section below. With that removed, `{G,R,A,B}` is **verified correct**: texel `0xFF00` reads back
> `R=255 G=0 B=0` on forced Vulkan, identical to D3D11. `Bgra4444` works.

**Unrelated bug noticed in passing:** forcing D3D11 *by name* is rejected. The ladder in
`SDL2_FNAPlatform.PrepareWindowAttributesWithFallback` exempts a zero return only when the
candidate is empty (automatic), on the stated assumption that "a named driver that succeeds always
sets `SDL_WINDOW_OPENGL` or `SDL_WINDOW_VULKAN`" — which is false for D3D11, the one driver that
needs no window flag. So `FNA3D_FORCE_DRIVER=D3D11` logs `declined (no window attributes)` and
falls through to OpenGL. Automatic still picks D3D11 correctly, so this only bites someone naming
it explicitly.

No `ApplicationPatcher.Version` bump and no reinstall — no patcher table changed and no IL is
rewritten. **A native binary changed, though**, so Android needs the APK repackaged rather than
just a managed rebuild.

### FNA3D's Vulkan descriptor pool grows until it fails, and then keeps going (2026-09-18)

**3D Brick Breaker Revolution** (`08aca837-50fc-df11-9264-00237de2db9e`) dies on Vulkan with
**904** first-chance `vkAllocateDescriptorSets VK_ERROR_OUT_OF_POOL_MEMORY` out of
`SpriteBatch.End` → `FNA3D_DrawIndexedPrimitives`, starting around `GraphicsDevice.Clear #2304`
and running to `#5833`, at which point the process is gone and `last_game_error.txt` says only
"the game process exited unexpectedly (native crash or force-close)". Measured on a Galaxy S24
Ultra (Adreno 750, Android 16).

**The crash is not the first failure — it is ~3,500 clears after it**, and that gap is the whole
story. `ShaderResources_FetchDescriptorSet` (`FNA3D_Driver_Vulkan.c`) refills its inactive list by
creating another pool of `nextPoolSize` sets and then **doubling `nextPoolSize` for ever**. Neither
call in that refill is checked:

- `VULKAN_INTERNAL_CreateDescriptorPool(...)` — return value ignored;
- `VULKAN_INTERNAL_AllocateDescriptorSets(...)` — return value ignored;
- `inactiveDescriptorSetCount = nextPoolSize` is then assigned **unconditionally**.

So once the device can no longer satisfy a doubling, the code hands out uninitialised
`VkDescriptorSet` handles and carries on drawing with them. `VK_ERROR_OUT_OF_POOL_MEMORY` appears
**nowhere in the driver except the error-to-string table** — there is no recovery path anywhere.
That is why the symptom is a long tail of logged exceptions followed by a native death rather than
a clean failure at the point of exhaustion.

**Why this title and not others.** It is a J2ME port (`mpp.javax.microedition.lcdui.Graphics`,
`iWrapper`, `RenderGuard`) whose `Graphics.fillRect` ends in a `SpriteBatch.End` **per primitive** —
the whole assembly contains exactly one `SpriteBatch.Begin` call site, so every rectangle is its own
batch flush and its own descriptor set. Expect the same from any other `J2XNA`/`mpp.*` port; a
title that batches normally will never reach the doubling.

**This is the case the crash breadcrumb explicitly cannot catch** — the game presents thousands of
frames before dying, so it is marked working at frame one. The escape hatch is the Settings picker:
OpenGL has no descriptor sets at all, and the game is confirmed playable there.

**Fixed in the vendored driver, 2026-09-18.** `ShaderResources_FetchDescriptorSet` now checks both
results, backs off by halving until the device accepts a pool, commits only what actually
succeeded, destroys a pool it could not allocate from, and caps the doubling at
`MAX_SAMPLER_DESCRIPTOR_POOL_SIZE` (256). On total exhaustion it returns `VK_NULL_HANDLE` and the
two call sites in `VULKAN_INTERNAL_FetchDescriptorSetDataAndOffsets` keep their previous binding
for that draw instead of binding garbage — a stale texture for one frame is survivable, an
uninitialised handle is not.

**One invariant to preserve if you touch this**: `inactiveDescriptorSetCapacity` is the RUNNING
TOTAL of every set a `ShaderResources` owns, not the size of the newest pool. The recycle loop in
`VULKAN_INTERNAL_ResetCommandBufferContainer` can return all of them at once and writes at
`inactiveDescriptorSetCount` **with no bounds check of its own**, so under-counting it is a heap
overflow. Getting that wrong during this fix produced exactly the same symptom as the original bug,
which is how easy it is to miss.

Measured on a Galaxy S24 Ultra: before, dead at `Clear #5833`; after, alive through a two-minute
soak at ~60 fps past `#5138`, zero `[wpr-ex]`.

**THE ROOT CAUSE WAS SHADER CHURN, AND THE REAL FIX IS MANAGED.** Instrumenting the driver showed
the game creating **1,792 `ShaderResources` in 50 seconds** — ~36 a second, without bound. One is
made per `MOJOSHADER_vkShader`, lives until that shader is deleted, and owns a descriptor pool and
its sets. No pool strategy survives that; it is arithmetic.

The churn comes from `SpriteBatch`: upstream builds a **fresh `Effect` in every constructor** from
the same immutable shader bytes. `J2XNA.dll`'s `Image.getGraphics()` and
`GameCanvas.getGraphics()` both do `new SpriteBatch(device)` on **every call**, with no caching and
no disposal — verified in the IL — and a J2ME paint loop calls that once a frame. The leaked
wrapper is a few bytes of managed memory holding a whole descriptor pool, so the GC feels no
pressure to collect it, and the device runs out.

**The fix is to make `SpriteBatch` own nothing per instance.** Three things moved onto the device —
`SharedSpriteEffect`, `SharedSpriteVertexBuffer` + `SharedSpriteIndexBuffer`, and the
`SharedSpriteBufferOffset` that indexes them — created lazily by the first batch and **not**
disposed by `SpriteBatch.Dispose` (the device frees them with its other resources, so a second
device in the same process gets its own set). `SpriteBatch.Dispose` now disposes nothing at all.

Safe to share because every write is immediately followed by the draw that consumes it, inside one
synchronous call on the game thread: the effect's MatrixTransform is written just before
`spriteEffectPass.Apply()`, and `UpdateVertexBuffer` is followed straight away by
`DrawPrimitives`. **Sharing the offset is required, not incidental** — two batches with private
offsets into one buffer would hand out overlapping `NoOverwrite` ranges.

**The buffers matter as much as the effect, and for a reason worth remembering: `~GraphicsResource`
is an EMPTY finalizer** (flibit's "FIXME: We really should call Dispose() here!"). So an undisposed
batch's ~220 KB of GPU buffers is never reclaimed — not by Dispose, not by the GC, not ever until
the device dies. Anything else that allocates GPU resources per instance and relies on the GC to
clean up is making the same mistake.

Measured on a Galaxy S24 Ultra: **`OUT_OF_POOL_MEMORY` 2,600 → 0**, menu draws, and the game is
**playable** — verified in-game at level 3/99. This helps every title that builds SpriteBatches in a
loop, on both drivers, and needs no repatch.

The driver changes above are kept as defence in depth — ignoring an allocation result was a real
bug and would bite another title — but they are no longer what makes this game work.

Measurements taken along the way, so they need not be repeated:

| checked | result |
| --- | --- |
| descriptor set recycling | **works** — `VULKAN_INTERNAL_CleanCommandBuffer` returns 122 sets per clean, `allocatedEver=3` command buffer containers, `submitted=1 inactive=0` stable |
| sampler pool refills | **never fail** — 862 refills in 45 s, every one granted at the first ask (`32->32`, `64->64`) |
| `ShaderResources_Init` sampler path (`samplerCount > 0`) | zero failures |
| adaptive `nextPoolSize` (remember the size that worked) | **no measurable difference** (1139 vs 1157); implemented, measured, reverted |
| growing `uniformBufferDescriptorPool` past its 1024 cap | implemented and kept — correct in its own right, but **did not change the error rate** |

By elimination the failures were in the one site left unguarded: the `samplerCount == 0` branch of
`ShaderResources_Init`, which builds a size-1 pool and a dummy set for every new shader. It is
still unguarded, deliberately — with the churn gone it is no longer reached, and guarding it would
only have hidden the symptom.

**The lesson worth carrying: when a Vulkan-only failure looks like pool exhaustion, count the
shaders before touching the pools.** Four rounds of driver work here moved the symptom around; the
one-line question "how many `ShaderResources` exist?" found the cause, and it was two layers up in
managed code.

**The NDK IS installed and FNA3D rebuilds cleanly — the "no NDK or cmake on this machine" claims
elsewhere in this file are stale.** NDK 27.3.13750724 plus the SDK's own cmake 3.22.1 (which ships
ninja). Roughly 2 seconds per ABI:

```powershell
$cmakeBin="C:\Android\Sdk\cmake\3.22.1\bin"; $ndk="C:\Android\Sdk\ndk\27.3.13750724"
& "$cmakeBin\cmake.exe" -G Ninja -DCMAKE_MAKE_PROGRAM="$cmakeBin\ninja.exe" `
  -DCMAKE_TOOLCHAIN_FILE="$ndk\build\cmake\android.toolchain.cmake" `
  -DANDROID_ABI=arm64-v8a -DANDROID_PLATFORM=android-21 -DCMAKE_BUILD_TYPE=Release `
  -DSDL2_INCLUDE_DIRS="<repo>\Src\Backends\FNA.Platform\lib\SDL2\include" `
  -DSDL2_LIBRARIES="<repo>\Src\Platforms\WPR.Platform.Android\Libraries\arm64-v8a\libSDL2.so" `
  -S "<repo>\Src\Backends\FNA.Platform\lib\FNA3D" -B <build dir>
& "$cmakeBin\cmake.exe" --build <build dir>
```

Two traps. **Configure only works as the first cmake invocation in a PowerShell block** — a second
one in the same block fails with `CMAKE_C_COMPILER not set, after EnableLanguage` from
`android-legacy.toolchain.cmake`; run one ABI per command. And **verify the swizzle table survived**
with `scratchpad/patch-swizzle.ps1 -WhatIfOnly`, which reports "already correct" against a good
binary — the shipped `.so` were binary-patched for that fix, so a rebuild is only safe while the
source carries it too (it does).

### FNA3D's Vulkan `passLock` was held for a whole render pass, and that deadlocks two drawing threads (2026-09-24)

**Plants vs. Zombies** (`706f822a-a47e-e011-986b-78e7d1fa76f8`, PopCap) sat on its PopCap logo for
ever on Android: no crash, ~0% CPU, nothing in logcat, and the per-game log simply stopped once the
loading thread finished its particle definitions. Windows plays it on D3D11 **and** on forced Vulkan,
and pushing the desktop's byte-identical patched `LAWN.dll` to the phone changed nothing, so it was
never the patcher.

**The cause is upstream FNA3D's "long-term lock".** `VULKAN_INTERNAL_BeginRenderPass` took
`renderer->passLock` and did not release it until the pass ended (`MaybeEndRenderPass` held the
matching unlock). So the thread with a pass open owned the whole renderer until its next target
switch or swap: every other thread's GPU call blocked on `passLock`, however long the owner then
spent in application code. PvZ draws from two threads — its loading thread renders into render
targets while the game thread draws the frame — and coordinates them with its own
`ResourceManager.DrawLocker`, which `Main.Draw` also takes. Measured: one thread inside
`FNA3D_ApplyEffect` waiting for `passLock`, the other holding it with a pass open and waiting on
`DrawLocker`. D3D11 serialises per call (as WP7's device did), which is why only Vulkan hung.

**The fix is in the vendored driver: `passLock` is now held per CALL.** `VULKAN_INTERNAL_LockPass`
replaces every `SDL_LockMutex(renderer->passLock)`; `BeginRenderPass` records the opening thread in
`renderPassThread` and releases the lock on return; and when a *different* thread takes the lock
while that pass is open, the pass is ended rather than waited on. The owner's next draw sees
`needNewRenderPass` and begins a fresh pass with LOAD semantics, so nothing is lost. `Clear` and the
three draw entry points now take the lock too, so command recording stays serialised — upstream only
got that serialisation as a side effect of the long-term hold.

Things worth knowing:

- **`libFNA3D.so` was rebuilt for all three ABIs** with the NDK recipe below (configure from **bash**,
  not PowerShell — the PowerShell invocation failed `CMAKE_C_COMPILER not set` even as the first cmake
  call), and all three still pass `scratchpad/patch-swizzle.ps1 -WhatIfOnly`. **The Windows
  `FNA3D.dll` was NOT rebuilt** (no MSVC build here), so forced Vulkan on the desktop still has the
  upstream behaviour. The desktop default is D3D11, where none of this applies.
- **The OpenGL driver hung this game even earlier**, on frame 2 — the `ForceToMainThread` queue from
  the graphics section above. **Superseded 2026-10-09**: with the GL worker contexts it crashed instead,
  and it now plays on OpenGL — see the next section.
- **Deferring WPR's post-Present `DiscardBackbufferContents` clear was tried and does not fix it.** It
  looked like the culprit (it opens a pass right after the swap), but the loader's own render-target
  `Clear` opens one just as well. Reverted.
- **How it was found, since the usual tools failed.** A Release APK is not debuggable, so no
  `run-as`/`debuggerd`, and `DOTNET_DiagnosticPorts` + `dotnet-dsrouter` never got a connection from
  the CoreCLR-on-Android runtime, even with the env var verifiably baked into the APK. What worked was
  a throwaway per-thread breadcrumb table (last GPU call per managed thread id) dumped by a
  background watchdog every 4 s into the per-game log. Two runs pinned both threads. Cheap to rebuild
  if another "silent hang, low CPU" shows up.

Verified on a Galaxy S24 Ultra, clean Release APK: logo → title → main menu → Level 1-1 and 1-2 played,
progress saved. Chickens Can't Fly and Cut the Rope still run on the new binary.

No `ApplicationPatcher.Version` bump and no reinstall. **A native binary changed**, so Android needs
the APK repackaged.

### On OpenGL a worker thread's DRAW calls run on the device thread (patcher v40, 2026-10-09)

Plants vs. Zombies crashed on a realme RMX3938 (Mali-G57) running OpenGL: `libEGL: call to OpenGL ES
API with no current context`, then SIGSEGV at `0x18` in `MOJOSHADER_effectCommitChanges` → `memcpy`.
FNA3D's GL driver defers the *resource* calls of a non-device thread (`ForceToMainThread`, or the
worker contexts) but runs the *drawing* ones (`SetRenderTargets`, `Clear`, `ApplyEffect`, the draws,
the state setters) on whatever thread calls them. On a worker there is no context, and MojoShader's
GL context pointer is **thread-local**, so the uniform-register map of a NULL context is written to.
PvZ's loader premultiplies every image by drawing it into a `RenderTarget2D` under
`ResourceManager.DrawLocker`; that is the whole reason it only ever worked on D3D11 and Vulkan.

**`WPR.Xna.Rhi.DeviceThreadDispatch`** (`WPR.Framework.Xna/Backend/`) runs such a call on the device
thread, synchronously, so it executes in issue order with the state the worker set. `FnaGraphicsBackend`
turns it on in `CreateDevice` only when the device is OpenGL (sysrenderer query), and every draw-family
member checks `DeviceThreadDispatch.MustMarshal` (OpenGL and off the device thread). It is drained at the
top of `Game.Tick` and before every `SwapBuffers`. Log: `[wpr-gputhread]`.

**The deadlock it would otherwise cause is solved in the patcher.** The worker holds `DrawLocker` while it
waits; `Main.Draw` blocks on `DrawLocker`; nobody drains. So v40 redirects `Monitor.Enter` (every C#
`lock` in game IL, both overloads) to `WPR.Xna.Compat.Monitor`, which on the device thread with dispatch
on spins `TryEnter(1 ms)` and drains between attempts. Everywhere else it is a plain `Monitor.Enter`.
This also covers the "game thread blocks on the worker" shape (Bejeweled) without relying on the native
worker contexts.

What goes where, and the one non-obvious rule:

| call | off the device thread, on OpenGL |
| --- | --- |
| draws, state, render targets, effects, queries, readbacks, renderbuffers | always handed to the device thread |
| **vertex/index buffer uploads** | **always handed over, Android included** |
| texture create/upload, buffer create | Android: stays on FNA3D's worker context (concurrent). Elsewhere: handed over, since `ForceToMainThread` would wait for a swap a lock-spinning device thread never does |

**Buffer uploads must follow the draws.** The worker's draws now run on the device context, which
already has SpriteBatch's shared vertex buffer bound, and GL only promises another context's writes are
seen after a re-bind. Left on the worker context, the draw used the previous sprite's vertices: PvZ's
title background came out shrunk into the top-left corner on the emulator's GLES. A new texture cannot
already be bound, which is why creation stays on the worker context.

GPU matrix, 2026-10-09:

| cell | result |
| --- | --- |
| D1 D3D11 | ✅ title → menu (reference) |
| D2 desktop Vulkan | ❌ pre-existing: the Windows `FNA3D.dll` lacks the passLock fix above |
| D3 desktop OpenGL | ✅ title → menu → level 1-1 (was a frame-2 hang) |
| D4 GLES 3.0 + no DXT | ✅ title → menu → level 1-1 |
| A1 emulator GLES 3.1 | ✅ title → menu → level 1-1, peashooter planted, sun 150 → 50 |
| A2 emulator Vulkan | ✅ level 1-1, seed packet picked up |
| P1/P2 S24 | not run — phone not connected |
| P3 Mali (the reporting device) | unverified — no device |

Montezuma (A1, GL) still reaches a live board. **Patcher table change (v40)**: `--repatch-installed` on
the desktop; Android repatches on next launch. Not identity-binding.

### State objects were never bound to the device, so games mutated the shared statics (2026-09-18)

`GraphicsDevice.BlendState`'s setter was a bare `nextBlend = value;`, and nothing in the repo ever
assigned `GraphicsResource.GraphicsDevice` for a state object. So `BlendState.GraphicsDevice` was
**null for every state object, forever** — the four predefined statics included. XNA's rule is the
opposite: assigning a state object to a device *binds* it and makes it read-only, which is why the
standard XNA idiom for editing one is

```csharp
if (state.GraphicsDevice != null) state = state.Clone();   // bound => read-only => clone first
// ... now safe to mutate
```

Under WPR that test always chose "mutate in place", and what got mutated was `BlendState.Opaque` /
`DepthStencilState.Default` — **the process-wide singletons every material starts out holding.**

**Kinectimals is the reference case** (`{5a3f9c59-1d30-4895-bb76-641bdd959a8c}`, Frontier). Its
material system is data driven: `EffectUtils.EffectPropertySet.Apply` walks
`Content/Core/EffectProperties.xml`, whose rules are matched against
`IAnimatedModelPart.MaterialName` — which is `(ModelMeshPart.Tag as string) ?? MeshName` — by
`name.IndexOf(m_str, StringComparison.InvariantCultureIgnoreCase) != -1`:

```xml
<Apply>                        <!-- NameContains="" : matches EVERYTHING -->
  <Blend><SetValue Name="DestinationBlend" Value="Zero" /></Blend>
<Apply NameContains="alpha">   <!-- matches the mesh named GrassyKnoll_Leaves_Alpha -->
  <Blend><SetValue Name="DestinationBlend" Value="InverseSourceAlpha" /></Blend>
  <DepthStencil><SetValue Name="DepthBufferWriteEnable" Value="false" /></DepthStencil>
```

With no clone both rules wrote to the same global object, so whichever model was applied last won —
and the default rule matches *everything*, so it kept resetting `DestinationBlend` to `Zero` and
stripping the leaves' blending. `AnimSystem.NativeModel.Draw` then also classified them as opaque
(`ColorSourceBlend == One && ColorDestinationBlend == Zero -> Opaque`) and drew them with depth
write on.

**Two things let this survive, and both are the general lesson.**

- **It is invisible on opaque geometry.** At alpha 1, `InverseSourceAlpha` and `Zero` produce an
  identical pixel, so a corrupted global blend state changes nothing anywhere except on genuinely
  alpha-blended parts. The world renders perfectly and one material looks broken.
- **It is bit-identical on every driver, and that is the tell.** Reported as a Vulkan bug on
  Android; reproducing it unchanged on OpenGL is what ruled the renderer out. **A rendering fault
  that is identical across two FNA3D drivers is not in FNA3D** — spend the next hour above the
  seam, not in it.

The symptom was "black around the leaves" because the leaf texture is **premultiplied** DXT5 whose
transparent region is exactly `(0,0,0,0)`: measured across all 1950 fully-transparent 4x4 blocks of
`GrassKnoll_Leaves_Alpha_0.xnb`, mean RGB `(0.0, 0.0, 0.0)`, max channel `0`. Draw that opaque and
you paint solid black. **Do not read a dark halo on foliage as a premultiplied-vs-straight-alpha
mismatch before checking which blend state actually reached the device** — and when you do test a
DXT5 texture for premultiplication, classify whole 4x4 blocks first: DXT5 compresses RGB and alpha
independently, so edge blocks bleed colour into transparent texels and a naive per-texel
`max(RGB) > A` test calls premultiplied content straight. It did here, on the first pass.

**The fix is `GraphicsResource.BindToGraphicsDevice(GraphicsDevice)`**, called from the
`BlendState` / `DepthStencilState` / `RasterizerState` setters on `GraphicsDevice` and from
`SamplerStateCollection`'s indexer. Two deliberate limits:

- **It does not go through the `GraphicsDevice` property setter.** That setter registers a resource
  reference for device-reset tracking, and the state singletons are **process-lifetime** while a
  device is **per game launch** — registering them would let the first game's device disposal take
  `BlendState.Opaque` down for every launch after it. Binding is one-way and first-writer-wins,
  which is XNA's behaviour anyway: a bound state object never changes owner.
- **XNA's read-only *enforcement* was deliberately not added.** Real XNA throws
  `InvalidOperationException` when you mutate a bound state object. Binding alone fixes this class
  of bug; adding the throw would convert other titles' latent mutations into hard crashes, so it
  stays unimplemented until something needs it.

**Expect this to have fixed more than one game.** Nothing about it is Kinectimals specific — "clone
it if it is bound" is a common XNA pattern, and every game using it was writing to the shared
singletons. Worth re-testing anything whose transparency, depth or sampler behaviour was reported
as wrong.

**Dead ends, recorded so they are not re-walked.** Every one of these was checked while chasing
this and is *correct*: `XNAToVK_SurfaceFormat` (`Dxt1` maps to `BC1_RGBA`, **not** `BC1_RGB`, which
would have been exactly this symptom), the swizzle table, `XNAToVK_BlendFactor` /
`XNAToVK_BlendOp` (both match XNA's enum order), the `blendEnable` derivation, `colorWriteMask`
(XNA's `ColorWriteChannels` bits are identical to `VkColorComponentFlagBits`), the `PipelineHash`
key and its full-key comparison, `texkill` translation (SPIR-V, GLSL, HLSL and Metal all test
`.xyz`), the SPIR-V unimplemented-opcode set (identical to GLSL's — all legacy ps_1_x),
`VertexFormatExpansion`'s SCALED conversion, and `DxtUtil`'s three CPU decompressors — which do
matter here, because Adreno/Mali support no BC format at all, so `Texture2DReader` decompresses all
734 of this game's compressed textures (480 Dxt5 + 254 Dxt1) on the CPU.

No `ApplicationPatcher.Version` bump and no reinstall — this is shim behaviour in
`WPR.Framework.Xna`, so games pick it up on next launch. No manifest or native change either, so
Android needs a managed rebuild only.

### On OpenGL ES, half of FNA3D's texture surface is a desktop-only lie (2026-09-21)

Android defaults to Vulkan, but three things put a device on the **OpenGL** driver — the Settings
GRAPHICS picker, the `GraphicsDriverProbe` demotion, and `fna3d_driver.txt` — and on Android that
driver is **always an ES3 context** (`FNA3D_Driver_OpenGL.c` forces `forceES3` by platform name).
Two whole classes of defect live on that path, and neither is Android-specific in origin: they are
desktop-GL assumptions in tables and entry points that nobody had re-read against the ES spec.
Found while investigating issue #43 (Asphalt 5 on a Mali GPU). **Neither is Asphalt 5's problem
alone** — one of them is breaking textures on the desktop today.

**Reproduce the whole GLES path on Windows. This is the finding that makes the rest cheap.**
FNA3D reads `FNA3D_OPENGL_FORCE_ES3` through `SDL_GetHintBoolean` and derives `useES3` from
`SDL_GL_GetAttribute(SDL_GL_CONTEXT_PROFILE_MASK)` on the context it just created, so

```powershell
$env:FNA3D_FORCE_DRIVER = "OpenGL"; $env:FNA3D_OPENGL_FORCE_ES3 = "1"
```

drives the exact `useES3` code path with no phone, no APK and no NDK. Verified on the Intel iGPU:
`MojoShader Profile: glsles`, `OpenGL ES 3.0`. **Use this before reaching for a device** for
anything GLES-shaped.

#### Defect 1 — `Bgra5551` / `Bgra4444` / `Bgr565` / `ColorBgraEXT` cannot be uploaded as mapped

`XNAToGL_TextureFormat[]` maps the two BGRA shorts to `GL_BGRA` and `XNAToGL_TextureDataType[]` to
`GL_UNSIGNED_SHORT_*_REV`; neither exists in any version of OpenGL ES, and
`GL_EXT_texture_format_BGRA8888` covers 8888 only. `Bgr565` is subtler and just as broken: internal
format `GL_RGB8` with type `GL_UNSIGNED_SHORT_5_6_5` — each legal on its own, the **pairing** not,
because ES3 admits that type only against `GL_RGB565`. There is **no extension check anywhere in
the driver**, so `glTexImage2D` never allocates storage and `glTexSubImage2D` is rejected too.

**Measured: with the conversion disarmed, a `Bgra4444` texture kills the process.** The harness
exits `0xC0000005` (access violation) on the first such texture under forced GLES3 — this is not
"wrong colours", it is the "game process exited unexpectedly" class.

**And a much bigger one was hiding above it, on every platform.** `Texture2D`'s constructor carried

```csharp
//Experimental! RnD / TEMP
if (format == SurfaceFormat.Bgra4444) { format = SurfaceFormat.Color; }
```

which changed `Format` without converting a single pixel. `SetData` then computed
`requiredBytes = w*h*GetFormatSize(Format)` = 4 bytes/px against the game's 2, tripped its own
guard and **returned without uploading**, behind a `Debug.WriteLine` invisible in a release build.
So every `Bgra4444` texture was blank on D3D11, Vulkan, desktop GL and Android alike. That is what
the "renders solid black on both Vulkan and OpenGL, cause unknown" note above was seeing — and with
it gone, FNA3D's `Bgra4444 = {G,R,A,B}` Vulkan swizzle is **verified correct** rather than assumed.

The fix is `Microsoft.Xna.Framework.Graphics.TextureFormatShim`: store as `Color` when the device
cannot take the requested format, converting both ways. Three things about it:

- **`Texture2D.Format` keeps reporting what the game asked for**, and `storageFormat` on `Texture`
  is what the driver sees. Reporting the substitute is exactly what the TEMP block did, and exactly
  why it dropped every upload. **This now covers DXT content too (2026-10-03, issue #43)** — see
  "Content textures report the format they shipped in" below; the old claim here that
  `Texture2DReader` could safely report `Color` because "it owns the texture's whole lifecycle" was
  wrong.
- **Round-trip is bit-exact** — 4-bit channels expand by `n*17` (`n<<4|n`, so `>>4` recovers `n`),
  5-bit by `x<<3|x>>2`, 1-bit alpha to 0/255 — because a game that reads a texture, edits it and
  writes it back must get its own bits.
- **Both write paths are hooked**, `SetData<T>` *and* `SetDataPointerEXT`. The latter is what
  `Texture2D.FromStream` and `TextureCube.DDSFromStreamEXT` use; missing it would leave every
  stream-loaded texture undefined.

#### Content textures report the format they shipped in, even when stored as `Color` (2026-10-03)

No Adreno or Mali GPU has BC formats, on either driver, so on every phone each `Dxt1`/`Dxt3`/`Dxt5`
texture is stored decompressed. `Texture2DReader` used to decompress it itself and create the
texture **as `Color`**, so `Texture2D.Format` said `Color`. Games can see that. **Asphalt 5** (issue
#43) treats `Format == Color` as "recolourable car body" (`bq.a`) and builds the CPU pixel buffer
only for the first sheet of each car. Every body (`20xx0`) is `Color`, but the interior and wheel
sheets (`20xx1`, `20xx2`) are `Dxt5`. Reported as `Color`, the wheel sheet was taken for a body with
a null buffer, and `m1.a(ref c5, bool)` threw an NRE on every frame of the race's loading screen,
which never finished. The `[wpr-fce]` frame names `m1.a`, because the throwing `bq.a(float…)` is
inlined into it.

The reader now creates the texture in its shipped format, and `TextureFormatShim` stores DXT as
`Color` when `FNA3D_SupportsDXT1`/`SupportsS3TC` say no, decompressing on upload through the same
`DxtUtil`. `[wpr-texfmt]` now prints `Dxt1=` and `Dxt3/Dxt5=`. A DXT texture stored decompressed
**cannot be read back** (there is no encoder), so `GetData` returns zeros and logs
`[wpr-texget] GetData refused: … (stored decompressed as Color)`. Real D3D11 also throws on
`GetData<byte>` of a DXT texture sized for RGBA, so no game can depend on that read. DXT **cube maps**
were never converted at all before this (`TextureCubeReader` has no decompression), so they now work
on phones too.

**Why the compat list said Android-playable:** the emulator's GL translator exposes S3TC, so the
conversion never ran there. **A phone-only texture bug will not reproduce on the emulator.**
**Reproduce one on the desktop with `WPR_FORCE_NO_DXT=1`** (makes `FnaGraphicsBackend.SupportsDXT1`/
`SupportsS3TC` answer no) plus `FNA3D_FORCE_DRIVER=OpenGL` + `FNA3D_OPENGL_FORCE_ES3=1`. Under those
three, the unfixed build reproduced the ticket exactly: streaky car body, loading bar stuck at ~20%,
982 NREs. The fixed build reaches a live race.

**The upside-down loading screen in the ticket is the same exception.** The game draws each loading
step as `SetRenderTarget(CGame1.e); hf.b(); SetRenderTarget(null);` and `hf.b()` is the frame that
threw, so the unbind never ran. FNA3D's GL driver Y-flips everything drawn while a target is bound
(`MOJOSHADER_glProgramViewportInfo(..., renderTargetBound)`), and `OPENGL_SwapBuffers` rebinds the
faux backbuffer after every swap without clearing `renderTargetBound`. So the frames went to the
screen with the render-target flip still on. **A whole screen mirrored top-to-bottom on GL means a
render target was left bound**, usually by an exception between bind and unbind. The desktop showed
black at that point rather than a flipped frame.
`scratchpad/dxtprobe` loads real Asphalt XNBs with DXT support switched off by reflection (`nodxt`)
and prints `Format`, the stored format and a screen hash per texture; run it from a folder holding
the desktop head's native DLLs.

No `ApplicationPatcher.Version` bump and no reinstall. This is shim behaviour.

#### Defect 2 — `GetData` on GLES is a NULL call, a silent no-op, or a heap overrun

`glGetTexImage` is `GL_PROC(NonES3, ...)` so its pointer stays NULL, and the only guard is an
`SDL_assert` compiled out of the release `.so`. But the crash surface is **narrower than that
suggests**, and there is something worse beside it — `OPENGL_GetTextureData2D` diverts `level == 0`
into `OPENGL_INTERNAL_ReadTargetIfApplicable`, which opens with
`if (texUnbound && !renderer->useES3) return 0;` and therefore **never declines on ES**:

| call | GLES behaviour |
| --- | --- |
| `GetData(level 0)`, 32-bit colour | **works** — FBO + `glReadPixels`, never reaches `glGetTexImage` |
| `GetData(level 0)`, narrower format | `glReadPixels` is hardcoded `GL_RGBA, GL_UNSIGNED_BYTE` and **ignores `dataLength`**, so it writes `w*h*4` into the pinned managed array. **Silent heap corruption** |
| `GetData(level > 0)` | NULL `glGetTexImage` — SIGSEGV at pc 0, no log |
| `TextureCube.GetData`, any level | no diversion at all, so the NULL call is unconditional |
| `Texture3D.GetData` | already a clean `FNA3D_LogError` into a managed throw |

`TextureReadback` refuses what the driver cannot answer, zero-fills and logs. **Zeros, not
fall-through — the opposite of `GpuBufferShadow`**, whose comment reasons that falling back "is no
worse than the behaviour it replaces". Here it is strictly worse: the alternative is a SIGSEGV or a
corrupted heap. XNA leaves a never-written texture undefined anyway, so zeros are within contract.

**Blast radius, measured: 15 of the 38 installed titles call `Texture2D.GetData`** — Mirror's Edge,
Kinectimals, NFS: Undercover, Skulls of the Shogun, Fragger, iGotham, MonstaFish, Monsters, Dream
Track Nation, G-Switch, Contre Jour, Fast and Furious: Adrenaline, Star Wars: The Battle for Hoth
and two more — plus Asphalt 5 (`bq::a`, twice). None calls `TextureCube.GetData` or
`GetBackBufferData`. That NFS: Undercover is on the list is worth holding against issue #40 ("stuck
at the loading screen on Android below 10", i.e. where Vulkan is least likely to be available).

**The CPU mirror was deliberately NOT built.** The texture twin of `GpuBufferShadow` is the obvious
fix and the economics are inverted: a buffer mirror is cheap and its crash was broad, whereas a
texture mirror roughly doubles a title's texture memory — the bulk of a WP7 game's footprint — on
precisely the devices that fell back to OpenGL because their Vulkan driver was misbehaving, to
serve a call most titles never make. Build it when a title proves it needs its pixels back; the
shape is recorded in `TextureReadback`'s doc comment.

#### Both facts come from one probe, and it measures rather than sniffs

`WPR.Backend.FNA.GlContextProbe`, called from `CreateDevice` beside `VulkanVertexFormatSupport`.
**Do not derive this from the driver name**: `SDL2_FNAPlatform.SelectedDriverName` is null under
automatic selection, and `"OpenGL"` does not distinguish desktop GL from GLES, which is the entire
question. Reading `SDL_GL_GetAttribute(SDL_GL_CONTEXT_PROFILE_MASK)` gives a bit-identical answer to
the driver's own `useES3` with no FNA3D change and no native rebuild.

**The two answers have opposite safe directions and the asymmetry is load-bearing.** Converting a
format that did not need it is lossless, so an unproven answer converts; refusing a readback that
would have worked returns zeros where a game expected pixels, so the guard arms **only on a positive
identification** of GLES — never on a probe failure, never on `#if ANDROID`.

Read it out of the log; both lines appear every launch including the no-op case, through
`XnaBackend.LogInfo` so they survive a Release build:

```
[wpr-texfmt] device texture formats - packed 16-bit (Bgr565/Bgra5551/Bgra4444)=UNSUPPORTED BGRA8888=ok (OpenGL ES (OpenGL ES 3.0 ...)); conversion ENABLED
[wpr-texget] texture readback - LIMITED (OpenGL ES (OpenGL ES 3.0 ...)); guard ENABLED
```

**Measured across all four configurations, identical numbers everywhere** (`scratchpad/texfmt`,
which round-trips texels *and* reads them back off the backbuffer — the two fail independently):

| | D3D11 | Vulkan | desktop GL | forced GLES3 |
| --- | --- | --- | --- | --- |
| `Bgra4444`/`Bgra5551`/`Bgr565` round trip | exact | exact | exact | exact |
| texel `0xF800` (`Bgr565` red) on screen | `255,0,0` | `255,0,0` | `255,0,0` | `255,0,0` |
| `GetData` level 1 | real pixels | real pixels | real pixels | refused, zeros, warned |
| `Alpha8` `GetData` | real pixels | real pixels | real pixels | refused, zeros, warned |

**The vendored `.c` was annotated, not changed.** Comments only — 44 insertions, 0 deletions, still
ASCII — at the two asserts, the `GL_RGBA /* FIXME: Assumption! */`, and the format tables. The
managed guard makes the native one unreachable, and turning the assert into a real
`FNA3D_LogError` would need a rebuild `FNA3D.dll` cannot have on this machine *and* would arrive in
managed code as a thrown `InvalidOperationException` that WP7 titles do not catch. **Source and
binary still agree behaviourally** — do not read these comments as a pending native fix.

**Also fixed in passing:** `Texture2D.SaveAsJpeg`/`SaveAsPng` sized their buffer from `Height` but
read back the caller's *output* `height`, so a scaled thumbnail encoded uninitialised heap below the
last row read. The only in-repo caller passes the texture's own size, so it was benign.

No `ApplicationPatcher.Version` bump, no reinstall, no manifest change and no native binary change —
shim behaviour in `WPR.Framework.Xna`, so games pick it up on next launch.

### The Vulkan validation layer is NOT shipped, on purpose (2026-09-05)

`libVkLayer_khronos_validation.so` used to sit in all three `Libraries/<abi>/` folders (~105 MB of
the Debug APK) and it **aborted the game process**: `FORTIFY: pthread_mutex_lock called on a
destroyed mutex` inside the layer's own bookkeeping, under
`VULKAN_INTERNAL_SubmitCommands` <- `VULKAN_SwapBuffers`, about a minute into play. Deleted, so
FNA3D takes the fallback it already has: `"Validation layers not found, continuing without
validation"`.

**Removing it did not make the emulator able to run a Vulkan-heavy game, and nothing will.** With
the layer gone, Fight Game Rivals got a little further and then took a SIGSEGV one level lower — in
`/vendor/lib64/hw/vulkan.ranchu.so`, the emulator's own gfxstream driver, null-dereferencing in
`get_host_u64_VkBuffer` while marshalling a `VkWriteDescriptorSet` from
`VULKAN_INTERNAL_FetchDescriptorSetDataAndOffsets` <- `VULKAN_DrawIndexedPrimitives`. That is
FNA3D's Vulkan driver meeting the emulator's gfxstream one. **Treat "renders on the emulator" as a
smoke test only: a game that draws its menus there may still die the moment it draws its actual
scene, and that crash says nothing about a device.**

**Re-read this in light of the 2026-09-07 driver switch.** When it was written, only the emulator
ever created a Vulkan instance — a phone kept `fna3d.env`'s forced OpenGL. **Every device now runs
Vulkan**, so in a Debug build every device asks for `VK_LAYER_KHRONOS_validation`
(`GraphicsDevice`'s ctor passes `debugMode = 1` under `#if DEBUG`, and FNA3D requests the layer
whenever it is set). Because the `.so` is not shipped, what every device gets is the graceful
fallback — that is the whole point of deleting the binaries rather than the request. Read the two
lines together in logcat; the second should **not** appear:

```
FNA3D Driver: Vulkan
Vulkan validation enabled! Expect debug-level performance!
```

If it ever does, someone has dropped the layer back into `Libraries/<abi>/`, and the abort above is
now reachable on real hardware rather than just on an emulator.

Removing the binaries rather than passing `debugMode = 0` on Android stays deliberate: `debugMode`
also turns on the OpenGL driver's `GL_KHR_debug` callbacks. That mattered more when phones ran GL;
it is now only relevant to a device put back on OpenGL through `fna3d_driver.txt`. Dropping the
layer disables Vulkan validation *only*.

If you ever need Vulkan validation back, drop the `.so` for the ABI you are testing into
`Libraries/<abi>/` locally — do not re-commit it — and expect the abort above to come with it.

### On a phone the game window is ALWAYS fullscreen (2026-09-05)

`SDL2_FNAPlatform.ApplyWindowChanges` used to honour `PresentationParameters.IsFullScreen` on
mobile, forcing windowed only on desktop. On Android that is not a neutral default, it is actively
destructive: SDL routes `SDL_SetWindowFullscreen` through `SDLActivity.setWindowStyle`, and the
**false** branch *clears* `FLAG_FULLSCREEN` and drops the immersive flags — beating
`MyTheme.NoActionBar`'s own `android:windowFullscreen`. A game whose `IsFullScreen` is false when
the device is created therefore keeps the status bar, the navigation bar **and** a surface inset by
both: it does not fill the screen. It is now `wantsFullscreen = IsMobilePlatform()` — a fact about
the device rather than a preference the game expresses, which is what WP7 actually was (XNA's
`IsFullScreen` defaulted to true on the phone and games treated it as decoration).

**The games this hits are not the ones you would guess**, which is why it survived so long. 23 of
the 25 installed titles set `IsFullScreen` in their `Game` constructor, i.e. before `CreateDevice`,
and were always fine. **Fight Game Rivals** (`{57b854f3-a3cc-4213-aa91-07aae56e146c}`) sets it from
`FGViewer`'s constructor — which runs during `Game.Initialize`, **after** the device exists — and
never calls `ApplyChanges`, so the value never reached a window. Measured on a 2400x1080 device:
black bars on all four sides plus both system bars, against Mirror's Edge filling the screen.
**Before blaming a game's own layout for "not full size", check whether it sets `IsFullScreen`
before or after `CreateDevice`** — a Cecil scan of the install folder for `set_IsFullScreen` and
`ApplyChanges` answers it in one pass.

Desktop is unchanged: `IsMobilePlatform()` is false there, so the window is still forced windowed
at the same single choke point every fullscreen transition funnels through.

Two things deliberately **not** changed. `GraphicsDeviceManager.IsFullScreen` still defaults to
false rather than true-on-mobile — the choke point already covers every path, including a game
that sets it false later, and a second default is one more place for the two to disagree. And
nothing sets `layoutInDisplayCutoutMode`: the vendored SDL Java carries no cutout handling at all,
so on a notched phone a landscape game is still letterboxed away from the cutout. Separate issue,
and it affects every game rather than this one.

No `ApplicationPatcher.Version` bump and no reinstall — this is backend behaviour in FNA.Platform,
so games pick it up on next launch.

### The backbuffer is discarded after Present, because DiscardContents is the default (2026-09-05)

`PresentationParameters.RenderTargetUsage` defaults to `DiscardContents`, and XNA's contract for
that is: **the surface holds nothing at the start of a frame.** A game that wants last frame's
pixels has to ask, by setting `PreserveContents`. `GraphicsDevice.SetRenderTargets` already applied
exactly that rule to every *offscreen* target — but nothing reset the **backbuffer** between
frames, so in practice it always behaved as `PreserveContents`.
`GraphicsDevice.DiscardBackbufferContents()` now clears colour, depth and stencil right after
`Present`.

**It only matters for a game that never calls `Clear`** — which is legal, and which WP7's
tile-based GPUs made free (each frame started from a blank tile). Most titles clear every frame and
cannot tell the difference; the extra clear is what they already issue.

**Fable: Coin Golf clears exactly zero times** and is the reference case. Both halves of a stale
frame hurt it, and **the depth half is the one that misleads**:

- **Stale depth occludes the new frame.** Its level drew *behind* the previous screen's geometry,
  lost the depth test, and the old screen simply stayed on display — the loading screen sat behind
  the dialogue for as long as the dialogue ran, and starting a level showed a black screen instead
  of the course. That is the "black screen on level start" the compat list recorded.
- **Stale colour accumulates.** The engine lays translucent passes down every frame (`CIwRenderable`
  fades set `BasicEffect.Alpha = 0.5`), and a 50% layer composited against its own previous output
  converges on black within a handful of frames: fades went to black instead of fading, and moving
  art left a hard-edged trail — on the dialogue screen, ~10 ghost copies of a sliding portrait card.

**The trap is that all of it reads as an alpha or blend-state bug.** It is not: the blend states are
right, `BlendState.AlphaBlend` is correctly premultiplied, `BasicEffect.Alpha` reaches the shader,
and every pixel is drawn correctly — against the wrong starting buffer. Two specific dead ends,
both walked on 2026-09-05: sampling the halo pixels shows pure black (1,1,1), which looks like
"alpha ignored, transparent texels drawn opaque" but is actually N compositing passes; and the
game's textures are DXT3/DXT5, which invites a premultiplied-DXT theory that goes nowhere.

**How to recognise it in one step:** `grep -c "GraphicsDevice.Clear #"` the per-game log. That trace
fires on the *first* clear and thereafter only when the clear colour changes, so **zero lines means
the game never cleared at all** — and any rendering weirdness that looks like "the previous screen
is still there" or "it fades to black and stays" is this. A game that clears logs at least one line.

Deliberately clears to **black in both configurations**, not the purple `DiscardColor` that
`SetRenderTargets` uses. Purple earns its place on an offscreen target — nothing ships it to the
screen — but the backbuffer is what the player looks at, and the normal loop here is a Debug build
judged by eye, so purple would repaint any game that leaves a margin unpainted and make Debug and
Release disagree about how a game looks. Black is also what the WP7 framebuffer actually started as.

Correct on both drivers: FNA3D's OpenGL `Clear` disables `GL_SCISSOR_TEST` around `glClear`, so a
game that left a scissor rect set still gets the whole surface reset.

No `ApplicationPatcher.Version` bump and no reinstall — this is device behaviour in
`WPR.Framework.Xna`, so games pick it up on next launch.

### `..` in an external content reference may escape the ContentManager root (2026-09-05)

`MonoGame.Utilities.FileHelpers.ResolveRelativePath` — the resolver behind
`ContentReader.ReadExternalReference` — was `Uri`-based, and **`Uri` clamps `..` at the root by
design** (RFC 3986 `remove_dot_segments`). Asset names arriving there are relative to a
ContentManager's `RootDirectory`, so a leading `..` that survives resolution is how content
legitimately addresses a **sibling of that directory**; clamping silently rewrote the reference to
name a file that does not exist. It is now a plain segment walk that keeps leading `..`.

**Fable: Coin Golf is the reference case.** Its ContentManager roots at `Content/data`, and every
level piece under `Content/data/pieces/` names its textures `..\..\<texture>` — i.e.
`Content/<texture>`, one level above the root, which is exactly where all 113 of those XNBs ship.
Clamping turned every one into `Content/data/<texture>`: 588 `ContentLoadException`s per run, and
the whole course drew untextured (a flat white/grey mass — *not* black; that was the separate
backbuffer bug above).

**The failure is invisible from inside the game**, because `ReadExternalReference` treats a missing
referenced asset as optional and hands back `default(T)`. That swallow is still there and still
wanted, but it means **"the XAP shipped without its content" is a conclusion to verify, not
assume** — the comment on that catch used to cite this very game as an example of a short XAP and
was simply wrong. Check the path we looked in against where the file actually is:

```powershell
# every "missing" name, against the content root one level up
grep -o 'missing referenced asset "[^"]*"' wpr_game_debug.log | sort -u
```

Dropping `Uri` also fixed two latent hazards in passing: it treated `#` and `?` in an asset name as
fragment/query delimiters, and these titles' texture names contain spaces (`MILL _0`,
`bridge to cross water_0`).

No `ApplicationPatcher.Version` bump and no reinstall — games pick it up on next launch.

### `TitleContainer.OpenStream` throws `FileNotFoundException` for a missing folder too (2026-10-23)

WP7's title storage threw `FileNotFoundException` for every name it did not have. .NET throws
`DirectoryNotFoundException` when a folder on the way is missing, and that is **not** a
`FileNotFoundException`, so a game's `catch (FileNotFoundException)` misses it.
`TitleContainer.OpenStream` now rethrows it as `FileNotFoundException` and keeps the original as
the inner exception.

**SPY Mouse** (`73E75EEE-…`, EA/Firemint) is the reference case. Its `ManagerFiles.open`
probes `res/<name>`, then its alt dir `res_960x640/<name>`, and catches only
`FileNotFoundException`. On every level start, `SceneGame.playAmbientScheme` asks for
`sound/sound/amb/<scheme>/amb.xml.brd`, and **the package has no `res/sound` at all**. The loop
`.xnb`s ship under `Content/sound/sound/amb/`, but no descriptor does, so the ambience never
played on WP7 either. The escape skipped `loadAssets()` for every object on the floor, then
`Draw` threw `ArgumentOutOfRangeException` on every frame. Play gave an empty screen. The hub
saw only the one `Game.Update` throw.

**The tell:** a hub `compat` group of `DirectoryNotFoundException` out of
`TitleContainer.OpenStream`. Tests: `TitleContainerTests`.

SPY Mouse reads input only from `Mouse.GetState()`, never `TouchPanel`, so the harness's `tap`
does nothing in it. A local `mtap`/`mswipe` verb (a `DispatchProxy` over `XnaBackend.Input`) drove
it to gameplay on D1. After the release it must keep reporting the injected position: if it
falls back to the real cursor, the GUI cancels the press.

### The patcher rescopes typerefs, and blobs are not typerefs (patcher v22, 2026-09-05)

A `typeof(...)` argument inside a **custom attribute** is stored in the attribute blob as an
assembly-qualified **string**, not as a row in the TypeRef table. `module.GetTypeReferences()`
never returns it, so every redirect the patcher performs — `Microsoft.Xna.* -> FNA`, the
`WprFrameworkXnaTypes` rescope, every `Patches` entry — had been walking straight past them since
the patcher was written. The attribute kept naming a WP7 assembly that does not exist at runtime.

`RescopeCustomAttributeTypeArguments` now walks every attribute on the assembly, module, types,
fields, properties, events, methods, their parameters and return types, and applies the *same*
`RescopeTypeReference` the table walk uses (it is a local function precisely so the two cannot
drift — a `typeof()` resolving somewhere its IL counterpart does not is the worst of both).

Three things about it are worth knowing before touching it:

- **Reading the arguments is the fix, not just the diagnosis.** Cecil parses a blob lazily and, if
  nothing touches `ConstructorArguments`/`Properties`/`Fields`, writes the original bytes back
  verbatim. That is exactly why the bug existed: the patcher renamed the assembly ref to `FNA` and
  the blob went on saying `Microsoft.Xna.Framework`. Touching them forces Cecil to materialise each
  argument into a `TypeReference` and to re-serialise from that model on write, so mutating the
  reference in place is enough.
- **A blob-parsed type does NOT share the module's AssemblyNameReference.** By the time Cecil
  parses the string there is no ref called `Microsoft.Xna.Framework` left to match — the rename
  already happened — so it mints a throwaway one for the dead identity. `assemblyScopesByOriginalName`
  (built *before* the rename loop mutates anything) maps it back onto the live instance, which is
  what makes every existing rename carry over to attributes for free instead of needing its own
  entry. The identity check keeps the table walk bit-for-bit unchanged.
- **An unresolvable attribute type is skipped, not fatal.** Cecil needs the constructor's signature
  to parse a blob, and WP7's `mscorlib, Version=2.0.5.0` cannot be resolved on any machine here, so
  `DebuggableAttribute` and `EditorBrowsableAttribute` throw. Their bytes are left untouched (which
  is the old behaviour, so no regression) and the distinct type names are reported **once per
  assembly** as `[attr-fixup]`. Do not restore per-attribute logging — it was hundreds of identical
  unactionable lines per install. A *game* attribute appearing in that list is the one case worth
  chasing.

**The failure mode is a hang, not a crash**, because in practice the attributes carrying `typeof()`
are `XmlSerializer` hints — `[XmlElement]`, `[XmlArrayItem]`, `[XmlInclude]`. Nothing loads the
type until a serializer is constructed over the declaring type, and the `TypeLoadException` then
arrives wrapped in `InvalidOperationException: There was an error reflecting type '…'`, which games
routinely swallow. **Fight Game Rivals** is the reference case: one
`[XmlArrayItem(ElementName = "Vector2", Type = typeof(Vector2))]` on
`GameObjectManager.BaseGameObject.CustomData.xmlValues` failed the serializer for
`Manager.xmlGameObjectSpecification`, which is how *every* screen in that game is deserialised, so
it sat on its splash screen for ever. The only trace was a first-chance exception in the per-game
log — grep `error reflecting type` there before concluding a stuck game is a timing bug.

**This was a patcher table change (v22), and affected games must be repatched.** Unlike v21 it is
not identity-binding — a v21 install still launches, it just keeps failing to build the affected
serializer — so `--repatch-installed` is enough. **The current version is 40**; see the next
section.

### Windows path separators in game file I/O (patcher v23), a patch that silently skipped (v24), and a game-specific IL guard (v25)

Three bumps, 2026-09-07 onward. None is identity-binding, so `--repatch-installed` is enough for
all three and no reinstall is needed.

**v23 — `Content\Credits.xml` opens on Android.** WP7 titles were built on Windows, where `\` and
`/` are interchangeable, so hardcoded Windows paths are everywhere: **7 of the 26 installed titles
carry one, and one carries 89**. On Android `\` is an ordinary filename character, so the open
fails, the game swallows it, and the symptom surfaces somewhere else entirely. **Battlewagon** is
the reference case — one failed `XmlReader.Create` left a field null and `TitleScene.Update` then
threw an NRE on every frame (5,593 in one run); its menu never built while the background animated
happily.

Four things about the shape of this fix:

- **It normalises at the point of USE, not by rewriting string literals.** A path assembled at run
  time — concatenation, `Path.Combine`, `"Level{0}\{0}.txt"` — is covered too. Windows behaviour is
  unchanged: the normaliser is a no-op when the platform separator is already `\`.
- **The rules are a platform capability, not a policy in the engine.** `caps.ContentPaths(...)` with
  `ContentPathRules { WindowsSeparatorsAreNative, ProbeInstallFolderForRelativePaths }`; the head
  states what its filesystem does and `WPR.Engine.Content.ContentPaths` decides what to do about it.
  **Omit it and the engine measures the running filesystem**, which is what keeps the bare
  game-host harness (no head, no composition) correct.
- **`MemberPatches` can only retarget a member's declaring type**, so every replacement must be
  substitutable for the original. `FileStream` / `StreamReader` / `StreamWriter` are unsealed, so
  `NormalizedPath*` subclasses work — the same mechanism `SharedIsolatedStorageFileStream` uses.
  `XmlReader.Create` needed its own entry because it resolves its argument as a URI and opens the
  stream inside `XmlDownloadManager`, where the `System.IO` entries never see the path.
- **NOT covered: `FileInfo` / `DirectoryInfo`.** Both are `sealed`, so no subclass can stand where
  the constructed instance lands. They would need a call-site rewrite like
  `RedirectIsolatedStorageOpens`; only 3 uses exist across the installed library, so it is
  deliberately left.

Note `XElement::Load(String)` already had an entry pointing at `XElement2` before this block
existed; only the `LoadOptions` overload was missing.

**v24 — no table changed; what changed is that assemblies which previously FAILED to patch now
succeed.** Cecil resolves a constant's declared type while writing the Constant table
(`MetadataBuilder.GetConstantType`), which on Android **always** failed because no managed assembly
is on disk there. `PatchDll` logged it and left that DLL unpatched — so the game still bound the
WP7 XNA identities and died at launch with a `FileNotFoundException` inside an
`AggregateException`. `ConstantEnumStubResolver` answers that resolve from the constant's own
recorded value.

**The bump exists purely so those installs repatch themselves.** An affected DLL is *pristine* on
disk, not stale, and nothing else can tell the difference — which is also why this went unnoticed:
a skipped assembly looks untouched rather than half-finished. Measured on a 36-game phone, two
titles had their **main** assembly skipped. A repatch removes this as a reason a game cannot start;
it does not promise the game then runs, and at least one of those two still does not.

**v25 — the second entry in `ApplyGameSpecificFixups`, and the first one that rewrites a method
body for a single title.** Feed Me Oil's `OggSound.StopById` ends in an unconditional
`_instances.RemoveAt(found)` that runs even when the search fell through and `found` is still -1.
It is reached because `SBSounds.playMusic` assigns `__lastMusicSound = soundId` **before** calling
`stopMusic()`, which resolves what to stop through that very field — so a change of track hunts for
the outgoing id inside the incoming sound's list and can never match. The fixup guards the remove;
reached with a guard it removes nothing, which is exactly right.

**Why one miss freezes the game permanently** is the part worth carrying forward, because the
symptom is generic and the cause is not. The throw happens inside `stopMusic` *before* it can run
`__musicId = 0`, so the stale id survives and the next frame takes the same branch for ever. It
escapes through `SBSceneObjects.onEnter` into `Director.Update` — which is where the game reads
`TouchPanel.GetState()` and dispatches `TouchBegan/Moved/Ended`, all of it below the throw. `Draw`
is a separate call and keeps running. **So the game paints a perfect frame at full rate while
nothing advances and no tap is ever seen**: "renders but is frozen and ignores input" is an
exception thrown once per frame out of `Update`, not a hang, and the per-game log's `[wpr-fce]`
lines are where to look. Identical on both heads — measured on a Galaxy S24 and on Windows, failing
at the same point (the story cutscene handing over to level 1).

Unlike v23 and v24 this one **does rewrite game IL**, so a pre-v25 install keeps the old body and
keeps freezing. Still not identity-binding: a v24 install launches fine.

**Feed Me Oil needed a shim fix too, and that one needs no repatch.** `BitmapSource` decoding was a
stub returning a 1x1 image with no pixels, and `WriteableBitmap.Pixels` handed back a fresh
zero-filled array of the wrong length (`height` ints, not `width * height`) — so writes went to the
GC and reads were sized wrong. `SBLevelData` builds its collision grid from
`res/data/level<n>.png`, so every level came out one pixel wide with an empty mask and the first tap
walked `AddActiveCollisions` off the end. Two details to keep: **`Pixels` are ARGB ints
(`0xAARRGGBB`)**, because games do `BitConverter.GetBytes(Pixels[i])` and index the little-endian
bytes as B,G,R,A — decoding yields RGBA bytes, so channels are reassembled rather than blitted; and
decoding **borrows `IGraphicsBackend.ReadImageStream`** (FNA3D's stb_image) rather than carrying a
second codec. That last one puts a `WPR.Framework.Silverlight -> WPR.Framework.Xna` reference in
the csproj — a peer edge under `Src/Core`, one-way and cycle-free, and *not* the backend reference
`BackendIsolationTests` guards.

### A missing shim type does not fail where it is missing (patcher v26, 2026-09-18)

`Microsoft.Xna.Framework.Media.Playlist` and `PlaylistCollection` were the **only two types of that
namespace WPR had never defined** — every other one is in `WPR.Framework.Xna/Media/` and listed in
`WprFrameworkXnaTypes`. A name that is in neither place keeps the patcher's blanket
`Microsoft.Xna.* -> FNA` rescope, and FNA deliberately defines no XNA API at all, so the game's
typeref resolves to nothing and the load throws `TypeLoadException`.

**The lesson is where the damage lands, not that the type was missing.** Fast and the Furious:
Adrenaline (`{ad67744a-…}`, Oberon/I-play) reads the playlist count in
`FF7Base.setNumberOfPlaylists()`, which its own constructor calls — and that constructor is the
root of a chain **fourteen objects deep** ending at `FF73DGame.createApplication`. The throw unwound
all of it, the Oberon SDK's `PlatformStub` was left holding no application, and `PlatformStub.Draw`
then threw a `NullReferenceException` on **every frame**. The player saw a white screen on load and
nothing else; the music library was never on screen and never suspected. **The symptom of a missing
shim is wherever the game's object graph happened to collapse to, which can be arbitrarily far from
the API that was missing.** Read the per-game log from the top and treat the FIRST `[wpr-fce]` of a
launch as the lead, not the loudest one — here the useful line was one `TypeLoadException` buried
under 7,874 identical NREs.

Three things worth keeping:

- **`grep -c "Game.Draw threw" wpr_game_debug.log` is the one-step test for this whole class.** A
  non-zero count means the game is alive and ticking but drawing nothing, so anything reported as a
  white, black or frozen screen is a broken object rather than a renderer problem. Pair it with
  `drawCallsThisFrame=` from the `Present` traces: `0` confirms it.
- **Missing MEMBERS bite exactly like missing types, and they bite at JIT time.** The same method
  also needed `MediaLibrary.Dispose()`, `AlbumCollection`'s indexer and `Song.Album`/`.Artist` —
  none of which WPR had. They are resolved when the method is compiled, not when the call executes,
  so the *other* arm of a `switch` the game never takes still has to exist. Dump the IL of the
  failing method and add everything it names, or you fix one throw into the next.
- **The shims stay permanently empty and that is correct.** There is no phone music library here, a
  device with no playlists is a state every WP7 title had to handle, and this game's "Music: Zune"
  option simply finds nothing. What it needed was for the *question* to be answerable.

**Patcher table change (v26), so affected games must be repatched** — `--repatch-installed` is
enough, and it is what rescopes the typeref in place. It IS identity-binding for a game that
touches playlists (a v25 install carries IL naming `[FNA]…PlaylistCollection`), but no IL body is
rewritten, so nothing needs a reinstall.

Verified end to end on both heads 2026-09-18: main menu, Quick Play, track select and a live 3D
race, with zero `Game.Draw threw` and zero `TypeLoadException` in the per-game log. The remaining
first-chance exceptions on that game are its own isolated-storage probes for a save that does not
exist yet, all caught by the game — do not chase them.

### One game, four unrelated blockers, and none of them where the symptom was (2026-09-19)

Chickens Can't Fly (`bd6d46cf-4177-4de0-93c3-610f450fc403`, Evozon/Microsoft Studios) was reported
as "crashes on boot". It took **four** independent fixes to reach gameplay, and the useful part is
that each one hid the next — a fixed boot crash that reveals a black screen that reveals a stuck
title screen is the normal shape of this work, not a sign of going backwards. Work down the log:
the next blocker is always already in it.

**1. The Windows head shipped no WCF, so any game touching `System.ServiceModel` died.**
`ApplicationPatcher` unconditionally rewrites a WP7 `System.ServiceModel` reference to
`System.ServiceModel.Primitives` **and adds a `System.ServiceModel.Http` reference to every patched
assembly** (`RescopeAssemblyReferences`). The Android head has carried both packages since it was
written; this head never did. The result was the crispest possible platform split — the identical
patched IL worked on a phone and took the desktop process down. The crash arrives as
`FileNotFoundException` on a **thread-pool thread**, i.e. unhandled and fatal, out of
`PreEmptive.SoS.Runtime.Access.Setup` — the Runtime Intelligence telemetry client Microsoft bundled
into the WP7 SDK, which `FallingGame.SetupAnalytics` kicks off from `Update` on a queued work item.
**5 of the 35 games installed locally reference `System.ServiceModel`** (Minesweeper, Cro-Mag Rally,
geoDefense Swarm, this one, Crimson Dragon), so this was never one title's problem. The packages and
their two security pins now match the Android head exactly; keep them in step, because the whole
point is that both heads load the same game IL.

**2. The cold-start `Activated` sent it down the wrong startup branch** — a `GameLifecycleQuirks`
entry, and a *different* failure from Doodle God's. Doodle God runs its init twice; this game runs
the **wrong init**. `FallingGame.pas_Activated` reads
`ActivatedEventArgs.IsApplicationInstancePreserved`, which WPR reports as `true` at boot on purpose
(Battlewagon needs it — see `HandleApplicationStart`), concludes it is resuming a fast app switch,
and sets `GameComponents.StartupMode = FastAppSwitching`. `LogoLoadingScreen.LoadGame()` then takes
the one branch that **skips `LoadStateFromDisk()`**, so `GameComponents.TombstonedState` is still
null when `LoadContent() -> ReactivateStaticData() -> ValueItemId.OnActivated()` dereferences it.
The two titles want opposite answers from one flag, which is exactly why that table is a list of
names.

**3. `Shared/ShellContent` does not exist in a WPR isolated store, and WP7's always did.** Fixed in
`SharedIsolatedStorageFileStream`, which now creates a file's parent directory for the modes that
create a file — see the long note on that type, including the measured fact that
`IsolatedStorageFile.CreateDirectory` does **not** sandbox its argument.

**4. `PropertyDictionary`'s read accessors NRE'd on a key nobody populated.** Under WPR the
leaderboard dictionary is always empty, so `GetProperty(key, false)` returns null and every read
dereferenced it. See the note on `PropertyDictionary.GetValueStream`.

**Blockers 2, 3 and 4 all presented as "the game renders perfectly and does nothing."** None
produced a crash, an error dialog or a single `Game.Draw threw`. The recognisers, in the order they
cost the least:

| symptom | what to grep | what it means |
| --- | --- | --- |
| stuck on one screen, still animating | `Game.Update (fixed timestep) threw` | an exception thrown once per frame out of `Update` — the Feed Me Oil pattern |
| stuck with **no** log growth at all | a `dotnet-stack report` | look for a game-owned worker parked on a `WaitHandle` |
| white/black screen, nothing moves | `Game.Draw threw` | the object graph collapsed; read the FIRST `[wpr-fce]`, not the loudest |

Blocker 3's tell is the second row: `LogoLoadingScreen` runs **its own drawing thread** for the
loading animation, and `JoinDrawThread()` is the line after the one that threw — so the thread sat
in `WaitOne` for ever and the per-game log simply stopped, thirty ticks in, with the title screen on
display. A live game thread plus a parked game-owned worker plus a silent log is that shape.

**Do not read `TouchPanel.INTERNAL_onTouchEvent` traces stopping as input stopping.** That trace is
capped at 30 events (`_wprTouchTraceCount < 30`), as are the tick traces (30) and the mouse-poll
trace (30). Half an hour went into "the taps are not arriving" before the cap explained it; the taps
were arriving the whole time and the misses were a coordinate-mapping error in the test harness.

**Verified end to end in the real launcher, 2026-09-19:** boot, main menu, SELECT A LAB, Hatchery,
experiment list, a played level with its bonus screen, and Back returning to the lab list — zero
`Game.Draw threw`, zero `Game.Update … threw`, zero NRE. The first-chance exceptions that remain are
all dead online services (`HttpRequestException`, `EndpointNotFoundException`, `SocketException`,
`GuidRetrievalException`) and the game catches every one; **do not chase them.**

No `ApplicationPatcher.Version` bump and no reinstall — three of the four are shim behaviour and the
fourth is a package reference, so games pick all of it up on next launch.

### The v28 `br` relocation broke shipped Android builds; the whole "Mono refuses IL" class is a Debug-build artefact (patcher v29, 2026-09-20)

> **Read this before the two v27/v28 sections below.** They were written against a Debug APK and
> are correct about what a Debug APK does. They are wrong about what ships.

Reported as "Earthworm Jim and Final Fantasy III worked in 0.1.03 and don't now". Both were
recorded Android-playable on 2026-09-05 (the 0.1.03 date) on the compat list. Measured on the
`Pixel_Dev` emulator, one game, one tree, patched four ways from the pristine XAP DLL:

| Earthworm Jim IL | Debug APK (interpreter) | Release APK (JIT) |
| --- | --- | --- |
| v21 (0.1.03 patcher) | `InvalidProgramException` on every `GSLogo.Draw`, never past the logo | title, menu, level 1 plays |
| v28, pass disabled | same as v21 | plays |
| v28, `br` extension off (= v27 = **v29**) | not measured | plays |
| v28 as shipped | `InvalidProgramException` at `b2Shape.Create` on level load | **never leaves the Gameloft logo**, no exception logged, 20% CPU |

Two conclusions, and they are separate:

- **Debug APKs run the Mono interpreter; Release APKs run the JIT.** `dotnet msbuild
  -getProperty:UseInterpreter` answers `true` for Debug and empty for Release, and nothing in the
  csproj changed since 0.1.03. The interpreter's IL importer refuses shapes the JIT accepts — the
  *identical* v21 bytes throw on every frame under Debug and play under Release. So every
  "InvalidProgramException with an empty message" measurement in the two sections below was a
  measurement of the interpreter, and **a Debug APK is not evidence about a phone running a
  release.** Judge Android on a Release build (`-c Release`; it installs over Debug because both
  use the debug keystore).
- **The v28 `br` extension is what regressed the shipped builds.** Its chained `br` clones hang
  Earthworm Jim under the JIT; without them (v27 output, now v29) the game plays. Final Fantasy
  III's patched IL is **byte-identical** for v21, v27 and v29 and differs only under v28 (three
  `br` blocks), so it is the same regression. The extension is now opt-in
  (`WPR_PATCHER_MONO_RELOCATION_BR=1`); the ret/throw half of the pass stays on, and Brain
  Challenge's coach still talks under a Release build with it.

**A second bug was hiding under the first: `PatchDll` destroyed the pristine original on every
Android auto-repatch.** It always read the *live* `.dll` and then did
`File.Move(dll, dll.original, overwrite: true)`. The desktop's `--repatch-installed` and the Android
Repatch button restore the sidecar first, so they were fine — but `GameLauncher.Launch`'s
"PatchedVersion is behind" path calls `Patch()` straight on the folder. Every version bump on a
phone therefore replaced `.dll.original` with the previous patched output and patched *that*
again. Five of fifteen emulator installs had a patched "original" (Earthworm Jim's was exactly the
v27 output, byte for byte). `PatchDll` now reads the sidecar whenever it exists and never
overwrites it — a second patch of the same folder is byte-identical to the first, verified — and
logs `[patch] <name>.original is NOT a pristine original` when the sidecar already references WPR
assemblies. **A phone that went through the v27→v28 bump has `br` clones baked into its
".original" and can only be recovered by reinstalling the game from its XAP**; the warning line is
how you tell. Check any install with:

```bash
grep -a -c WPR.Framework.Xna <install>/<game>.dll.original    # 0 = pristine
```

**Diagnostic switches, so the next A/B is a repatch and not a rebuild:**
`WPR_PATCHER_DISABLE_MONO_RELOCATION=1` skips the pass entirely;
`WPR_PATCHER_MONO_RELOCATION_BR=1` turns the v28 extension back on. `scratchpad/patchone` patches
one DLL in place with the real `ApplicationPatcher` (build it against `WPR.Loader`); `adb push` the
result over the game's `.dll` and start it with

```bash
adb shell am start -n com.wpr.android/.GameShortcutActivity --es wpr.shortcut.ProductId <ProductId>
```

which goes through the same `GameLauncher.Launch` as a tap. **Under a Release APK there is no
`wpr_game_debug.log` and no `[wpr-fce]`** — both are `#if DEBUG` in `ApplicationLaunch` — so
judge progress from the screen, `pidof com.wpr.android:game`, and the `libOpenSLES` lines in
logcat (Earthworm Jim opens audio when it leaves the logo).

**Patcher table change (v29), so v28 installs must be repatched** — `--repatch-installed` on the
desktop, automatic on next launch on Android (and safe now that the launch path no longer eats the
original). Not identity-binding.

### MonoVM refuses a method because of what SITS AFTER a `br` (patcher v27, 2026-09-19)

> **Superseded in its premise by the v29 section above**: the `InvalidProgramException` here is
> the Mono *interpreter*, i.e. a Debug APK. The pass is still on for ret/throw blocks because it
> is cheap and Brain Challenge plays with it; the `br` extension (v28) is off.

**The single most widespread Android-only defect found so far: 5,000+ affected blocks across 32
assemblies in a 36-game library.** Mono's IL importer carries the evaluation-stack state *linearly*
into the instruction that **follows an unconditional branch**. When that instruction happens to
begin a block entered from somewhere else at a different depth, the two states conflict and Mono
refuses to compile the **whole method** — `InvalidProgramException` with an **empty message**.

Everything about it is designed to waste your time:

- **The IL is legal.** Nothing ever falls through the `br` into that block, and every real
  predecessor enters it at the depth it expects. **CoreCLR compiles it fine and ILVerify passes it
  clean**, so Windows is unaffected and no static tool will point at it.
- **The exception names the CALLER, not the broken method.** A method that fails to JIT throws at
  its call site, so the top frame is whatever called it. Brain Challenge reported `ds.d` and
  `bj.ey`; the methods that actually failed were `ds::en`, and one of `bj::ew`/`ex`/`em`/…
- **The message is empty**, so there is nothing to grep for but the type name.
- **It kills `Update` while `Draw` keeps running**, so the game renders a perfect frame at full
  rate, ignores every tap, and never advances. That is the same end-user symptom as the Feed Me Oil
  freeze and as a stuck loading thread — see `[wpr-fce]` in the per-game log to tell them apart.

**Obfuscators produce this shape constantly**, which is why the count is so high. Control-flow
flattening emits one giant `switch` over a state local with every arm ending in
`stloc state; br dispatch`, so arms sit back-to-back — and an arm that leaves the stack non-empty is
immediately followed by the *next* arm's entry point. Nothing about it is specific to one
obfuscator or one game.

**The fix is `ApplicationPatcher.RelocateMonoStackConflictBlocks`**: copy the affected block to the
end of the method and repoint its entrants at the copy. The original stays put and simply becomes
unreachable — and **Mono does not import unreachable code, which is exactly why this cures it.**

**COPY, NEVER MOVE.** Moving the block makes whatever followed it the new neighbour of that same
`br`, which re-creates the defect somewhere else. Measured: a move-based version fixed the two known
failures in Brain Challenge and introduced **2,816 new ones** in a single method (`ay::h`).

Three restrictions keep it safe, and **each one was a real ILVerify failure first** — do not relax
any of them without re-running the sweep below:

| restriction | what happens without it |
| --- | --- |
| skip methods with exception handlers | the clone lands outside the try/handler it came from — `BranchOutOfFinally` |
| the method must already end in `ret`/`throw` | the clone becomes reachable by fallthrough at the wrong depth — `PathStackDepth` |
| a block must have no second entry point | repointing its entrants strands whoever jumped into the middle of it |

**A block may end in `ret`, `throw` or `br` (the `br` case since 2026-09-19).** `ret`/`throw` end
the flow, so whatever is appended next starts clean — that is why the method must itself end in one
of them, and why the first clone is always safe. A `br` hands its own depth straight on, so the
clones are **ordered** rather than merely filtered: every `ret`/`throw` clone first, in any order,
then the `br`-terminated ones as a chain where each one's exit depth equals the next one's entry
depth. A block that cannot be chained is left alone. That ordering is what still makes a **single
pass** sufficient — no clone can create a new candidate, so there is no fixed point to iterate to.

`br` was excluded outright before, and it was not a corner: Earthworm Jim's
`b2PolygonShape::.ctor` has four conflicting blocks and **all four end in `br`**, so the whole
method was skipped.
`SimplifyMacros`/`OptimizeMacros` runs on each modified body afterwards, because a cloned short
branch now sits too far from its target for the one-byte form (`BadJumpTarget`).

**`CloneInstruction` returns null rather than guessing**, and the caller then leaves that whole
block alone. Note `ldtoken` (`InlineTok`) may carry a **type, a field OR a method** — assuming the
type case is the easy way to corrupt an assembly here.

**The regression test that matters, and the one to repeat after any change to this pass:** ILVerify
every patched assembly with the pass on and with it off, and diff. The call site is one line, so
commenting it out, rebuilding and re-running `--repatch-installed` is a ten-minute round trip.
Measured 2026-09-19 over the whole installed library: **161 assemblies, 32 with errors, byte-for-byte
identical counts both ways** — every one of those 32 is a pre-existing `MissingMethod` /
`ClassLoadGeneral` / native-int category, none of them this pass. A green build proves nothing here.

**Proven, not reasoned.** The A/B that established the rule: an identical three-instruction block
reachable only through a switch **fails** sited after a `br` that left depth 2 and **compiles**
sited after a `br` that left depth 0. Eliminated along the way and not worth re-testing: block
content, fields touched, switch width, switch opcode, enum-on-stack, block size, block count, slot
index, position alone, the `LoadFromStream` path, interpreter vs JIT, and language/culture.

**Runs unconditionally, not per-platform.** One patched install is shared between the heads, so a
game patched on a PC and copied to a phone has to carry it. Windows is unaffected either way.

**The `br` extension does NOT fix Earthworm Jim, and that is the honest limit of this model.** With
all four blocks of `b2PolygonShape::.ctor` relocated the method is clean by the pass's own model and
MonoVM *still* refuses it — `InvalidProgramException` with an empty message, surfacing at
`b2Shape::Create` (its only caller) on every level load, which then takes the half-built level into
a `SIGSEGV` in `MOJOSHADER_effectBeginPass`. So something beyond a br/entry-depth disagreement also
upsets Mono here and is **still unidentified**. Do not assume the model is complete because the
Brain Challenge cases it was built from are cured.

**Two dead ends from 2026-09-19, recorded so they are not re-walked:**

- **Widening the trigger to "carried depth differs from entry depth, in either direction"** — i.e.
  also taking the case where the `br` left the stack *empty* and the block wants more. It looks
  principled and it is wrong: it flags **25,918** sites across the installed library, all through
  Angry Birds, Pac-Man and Guitar Hero 5, which run on Android today. A predicate that fires on tens
  of thousands of shapes Mono demonstrably accepts is not describing the defect. Reverted.
- **The obfuscator's dead `ldc.i4.0; br` pair** (the opaque-predicate leftover sitting unreachable
  after a `br`) is **not** the trigger either. `GSLogo::Draw` carries it, is untouched by the pass,
  and runs fine.

**A cheap way to see the shape in one method**, without running anything: walk the body computing
entry stack depths, then for each branch target compare its entry depth against the depth carried
from the preceding instruction. Mismatch = candidate. That is `ComputeEntryStackDepths`' own model,
and reproducing it in a script is what turned "Mono hates this method" into four numbered blocks.

**There is no Mono-side diagnostic to be had, and this was settled by running it, not by reading
about it.** The shipped runtime's IL importer logging is compiled out. Verified 2026-09-19:
`MONO_LOG_LEVEL=debug` + `MONO_LOG_MASK=all` genuinely in effect (logcat: `Env variable
'MONO_LOG_LEVEL' set to 'debug'`) produces **141,288 lines of gref/lref tracking and not one word
about IL** — no method name, no offset, no reason. The exception stays empty-message. Two traps
worth knowing if you try again:

- **`setprop debug.mono.env` loses to the build's own environment.** The app's generated environment
  already sets `MONO_LOG_LEVEL=info`, and that wins. The only way to raise it is a temporary line in
  `fna3d.env`, which is `@(AndroidEnvironment)` — and that means a full APK rebuild per attempt.
- **Android Debug builds run the INTERPRETER, not the JIT** — logcat says `Mono AOT mode: interp`
  right at startup. So the importer at fault is `interp/transform.c`, not the JIT's. Worth keeping in
  mind when reading Mono source for this, though interpreter-vs-JIT was already eliminated as the
  discriminator for the Brain Challenge cases.

**So narrow it by bisecting on the device instead — stub a method body and see if the throw moves.**
That is how `b2PolygonShape::.ctor` was confirmed: replace its body with
`ldarg.0; ldarg.1; call b2Shape::.ctor(def); ret` and push the assembly. The level load then reaches
`LoadPhyEnv` with **zero** `InvalidProgramException`, where the real body gives exactly one. That
rules out `b2CircleShape::.ctor` and `b2Shape::Create` — which is where the throw is *reported* —
and puts the fault inside the polygon constructor beyond argument. (Expect a crash further on: a
polygon with no vertices is the stub's own doing, not a finding.)

**What that leaves, for whoever picks this up.** The method is clean on the slot-count model above
and Mono still refuses it, so the model is missing something the interpreter checks. The most likely
candidate is that `transform.c` compares stack *state*, not just depth: this constructor is full of
`ldelema` / `ldobj` / `stobj` on `Vector2`, and two paths can agree on how many slots are live while
disagreeing on what is in them. Testing that needs type-aware stack modelling, which the pass does
not have. Do not bisect by truncating the body — the control flow is switch-flattened, so a prefix
is not a valid method; neutralise one switch arm at a time instead.

**ILVerify after any change to this pass — it is cheap and it is the only real check.**
`dotnet-ilverify` is installed globally here:

```bash
ilverify <patched>.dll -r "<install dir>\*.dll" -r "<desktop output>\*.dll" -r "C:\Program Files\dotnet\shared\Microsoft.NETCore.App\8.0.30\*.dll"
```

Measured 2026-09-19 on `EarthwormJim.dll`, the assembly the extension changes most (980 blocks
across 310 methods): **"All Classes and Methods Verified"** both before and after.

**Patcher table change (v27), so affected games must be repatched** — `--repatch-installed` is
enough. It rewrites game IL, so a v26 install keeps the old bodies and keeps throwing; it is not
identity-binding, so a v26 install still launches.

### Cecil does not preserve TypeDef row ids, and an obfuscator keys on them (patcher v33, 2026-09-22)

`ApplicationPatcher` re-emits every assembly through Cecil, and **Cecil renumbers the TypeDef
table**: it writes types depth first, each one immediately followed by its nested types, so an
assembly whose original table interleaved them any other way comes back with different metadata
tokens even though not one type changed. Measured on The Treasures of Montezuma
(`56a2bd8b-af90-4575-b25f-97b31a179422`, Alawar) — the obfuscator's helper type moved from
`0x02000007` to `0x0200000b`, and eleven of its twenty-one types shifted.

**Ordinary game code cannot tell. Eazfuscator.NET can, and it turns the difference into a file
offset.** Its string decryptor hashes the assembly's public key token, its simple name and the
**`MetadataToken` of four of its own helper types**, then uses the result to seek the encrypted
string blob it loaded with `GetManifestResourceStream`. A renumbered table yields a wrong key, and
the game dies on the very first string it decrypts:

```
System.ArgumentOutOfRangeException: value ('-180695550') must be a non-negative value.
   at System.IO.UnmanagedMemoryStream.set_Position(Int64 value)
   at .(Int32 )
   at Alawar.TheTreasuresOfMontezuma.Game..ctor()
```

**The value is deterministic and byte-identical on both heads** — it is a property of the patched
file, not of the run — which is the cheapest way to tell this apart from anything environmental.
Reported from a phone, reproduced unchanged on Windows at the first attempt.

**The fix is `PreserveOriginalMetadataTokens`**: it records every TypeDef's pristine token in an
embedded resource (`WPR.OriginalMetadataTokens`) and retargets every `get_MetadataToken` call site
to `WPR.WindowsCompability.OriginalMetadataTokens.Resolve`, which reads it. `callvirt` becomes
`call` with the instance as argument zero — the same call-site shape `RedirectIsolatedStorageCalls`
uses, so the evaluation stack is untouched.

Four things are deliberate:

- **Keyed by NAME, not by the new token**, so the pass never has to predict what Cecil will do. A
  type whose token did not move is simply absent from the table and falls through to its real one.
  Recording all of them costs nothing here — the largest affected assembly has 571 types, about
  10 KB.
- **Types only.** MethodDef and FieldDef rows are renumbered by the same rewrite; nothing in the
  library keys on them, and a table covering them would dwarf the assembly. A non-`Type` member
  falls through unchanged, which is the behaviour that was already there.
- **A module that also calls `Module.Resolve*` is skipped entirely.** Such a module feeds tokens
  back to the runtime, and half a remap is worse than none. Measured across all 307 XAPs: every
  `Module.Resolve*` call site in the library is in **UnityEngine.dll** and nowhere else.
- **A torn or missing table degrades to the real token** rather than throwing.

**Blast radius, measured: exactly two games ask for a metadata token at all** outside UnityEngine —
this one and **Farm Frenzy 2**, the same Alawar/YF engine, eight obfuscated assemblies each. The
repatch log is the check, and it says nothing for the other 46 installed titles:

```
[token-fixup] YF.Framework.Render.dll: 1 get_MetadataToken call site(s) now read the pristine token of 99 type(s).
```

**Patcher table change (v33), so affected games must be repatched** — `--repatch-installed` on the
desktop, automatic on next launch on Android. It rewrites game IL and embeds a resource, so a v32
install keeps the broken key and keeps throwing; not identity-binding, so a v32 install still
launches.

**The general lesson is worth more than the fix.** Anything a game reads that describes *the shape
of its own assembly* — metadata tokens, table counts, an image checksum, an MVID — is something the
patcher may have changed, and none of it produces a diagnosable error: it comes back as a wrong
number that some later arithmetic turns into a crash somewhere unrelated. **An unprintable frame
like `at .(Int32 )` next to a `System.Reflection` or `GetManifestResourceStream` call means
"obfuscator", and the first question is what it is hashing.**

### The same obfuscator also checks WHO CALLS IT, and CoreCLR-on-Android fails that (patcher v35, 2026-09-24)

`X0X` is not a string. It is Eazfuscator.NET's tamper sentinel, hardcoded in the string
decryptor:

```csharp
if (m_\u0006 == 43962)                                   // 0xABBA
    return new string(new char[3] { 'X', '0', 'X' });
```

Once that flag is set **every** string in the assembly decrypts to it. The Treasures of Montezuma
died in `Game..ctor` with `ArgumentException: An item with the same key has already been added.
Key: X0X` — `MouseDevice..ctor` registers two axes under two different string ids, both came back
`X0X`, and the second `Dictionary.Add` threw. **The duplicate key is three layers below the
actual fault; do not start debugging at the dictionary.**

**The protection is a caller-identity check, and there are two of them.** Each walks
`new StackTrace()` to a **fixed frame index** and demands that frame's declaring type live in the
decryptor's own assembly:

```
frame.DeclaringType == typeof(RuntimeMethodHandle) -> flags |= 4    (called via reflection)
frame.DeclaringType == null                        -> flags |= 1
frame.DeclaringType.Assembly != mine               -> flags |= 2
else                                               -> flags |= 16   <- the only good outcome
```

The sentinel fires unless bit `16` is set. **The second check is the one that matters most**, and
it is easy to miss: a parameterless `static bool` using `GetFrame(3)` whose result is XORed into
the **decryption key**. So suppressing only the sentinel is *worse than doing nothing* — the
poison stops, the key is still wrong, and the blob reader dies with
`EndOfStreamException: Attempted to read past the end of the stream`. Measured, in that order,
while chasing this.

**This is NOT the v33 bug, despite the same obfuscator and the same game.** v33 repairs an input
*WPR itself perturbs* (Cecil renumbers the TypeDef table). This is a check WPR does not touch at
all: **the patched bytes are irrelevant**. Proven by pushing the desktop's byte-identical patched
assemblies onto the phone — desktop plays, phone still poisons. What differs is the managed stack
the runtime reports.

**Eliminated by measurement — do not re-walk these:**

| hypothesis | result |
| --- | --- |
| the phone's patched bytes differ (it repatches on-device) | they did; pushing desktop's exact bytes **failed identically** |
| the assembly is loaded twice, so the two `Assembly` instances differ | each game assembly is probed **exactly once** in logcat |
| JIT inlining moves the frames | marking **all 8,993** methods across its 14 assemblies `NoInlining` **changed nothing** |

So it is the stack walk itself: CoreCLR-on-Android does not hand back the frame Eazfuscator
expects. That is also why it only appeared with the .NET 10 move — desktop (.NET 8, x64) satisfies
the check and always did.

**The fix is `ApplicationPatcher.NeutraliseObfuscatorStackIdentityChecks`**: force both checks to
their **trusted** outcome — the answer the runtime was going to give on desktop anyway, which is
what makes it safe there. The parameterless `static bool` becomes `return true`; the
`caller.Assembly == mine` test becomes `pop; pop; br <equal path>` (both operands are on the stack,
so the branch cannot merely be deleted).

Three things are load-bearing:

- **The predicate needs all three signals** — a `StackTrace.GetFrame` call, a
  `typeof(RuntimeMethodHandle)` comparison and a `Type.Assembly` read. Any one alone is something
  ordinary code does; a logger that walks frames has the first and neither of the others.
- **Cecil's `Replace` does not repoint branches at the instruction it removed**, and an obfuscated
  method is nothing but jumps. `RepointBranches` fixes every branch, switch case and exception
  handler bound. Skip it and you write a body that fails verification rather than throwing at
  patch time.
- **`SimplifyMacros` / `OptimizeMacros` around the edit**, same reason as
  `RelocateMonoStackConflictBlocks`: the rewrite grows the body and a short branch elsewhere may
  no longer reach its target.

**Scope, measured: 1 of 48 installed titles.** Nine of Montezuma's assemblies carry the check
(`ldc.i4 43962` = bytes `20 BA AB 00 00`, which is how to scan for it). Farm Frenzy 2 is the likely
second, being the same Alawar/YF engine. The pass logs `[eaz-fixup]` per assembly and says nothing
for everything else.

**ILVerify is the check after any change here**, as with the Mono relocation pass: all 14 of the
game's assemblies verify clean **both before and after**, and only a baseline *diff* means
anything (about 32 assemblies in the library fail verification already).

Verified end to end: Montezuma reaches a live match-3 board on the phone with patcher-produced
bytes, and the same rewritten assemblies still run on Windows.

**Patcher table change (v35), so affected games must be repatched** — `--repatch-installed` on the
desktop, automatic on next launch on Android. Rewrites game IL, so a v34 install keeps the poisoned
strings; not identity-binding, so a v34 install still launches.

### Cecil needs FNA.dll as a FILE, and .NET 10 stopped shipping one (2026-09-24)

`ApplicationPatcher` reads the assembly it is patching from a stream, but Cecil's
`BaseAssemblyResolver` only finds **real files** in its search directories — and inside an APK the
runtime's assemblies are not files. `WprStartup.SetupDllPatchForCecil` exists to put `FNA.dll` on
disk and make that folder the process CWD.

It used to get those bytes back out of the APK's own assembly packaging: a
`assemblies/FNA.dll` zip entry in Debug, `AssemblyStoreExplorer` in Release. **That packaging is
not a stable contract.** Under .NET 10 assemblies live in `lib/<abi>/libassembly-store.so`, and
`_AndroidUseAssemblyStore` is force-set true whenever `EmbedAssembliesIntoApk` is, so it cannot be
turned off. The reader found nothing and logged

```
[Android] Fail to copy DLL $FNA to patch assembly folder (entry not found)!
```

**The failure is quiet, and that is the point.** Patching still "succeeds" — the game launches,
it just binds against an assembly Cecil could not resolve. So every install and repatch performed
**on the device** was suspect, while the same game patched on the desktop was fine. That is why
the phone's copies of a game are byte-different from the desktop's despite identical pristine
originals.

`FNA.dll` is now shipped as a plain **`AndroidAsset`** (`assets/PatchAssemblies/FNA.dll`, wired by
the `_WprStageFnaAssetForCecil` target) and copied out like the seed databases. Nothing parses the
APK's internals any more, so no future change to assembly packaging can break it.

Four details worth keeping:

- **Sourced from `@(ReferencePath)`, not a hardcoded bin path**, so it follows configuration and
  TFM — and the target `<Error>`s if FNA ever stops being referenced, rather than silently
  shipping no asset. It is the **implementation** assembly (232,960 bytes), not the 184,320-byte
  ref assembly; a ref assembly would resolve types but is not what the patcher should see.
- **One code path for Debug and Release.** The old `#if DEBUG` split meant the configuration most
  testing happens in exercised different code from the one that ships — the same trap as the
  interpreter-vs-JIT split.
- **Copied unconditionally, not only when absent.** The staging folder is in external files and
  outlives the install, so a skip-if-present would leave the *previous* APK's FNA.dll behind after
  an update and patch games against the wrong build.
- **`assembly-store-reader` is gone** (deleted 2026-10-13): nothing referenced it after this change.

No `ApplicationPatcher.Version` bump for this half. **A manifest/asset change**, so Android needs
the APK repackaged rather than just a managed rebuild.

### WP7 effect blobs may NAME a stock effect instead of containing one (2026-09-22)

With the tokens fixed, Montezuma got as far as `GraphicsDeviceManager.CreateDevice` and died there
on `MOJOSHADER_compileEffect Error: Unexpected EOF`, out of `Effect..ctor(GraphicsDevice, byte[])`.
The blob is **20 bytes**:

```
cf 0b f0 bc   0xBCF00BCF   the XNA4 container MojoShader already knows to skip
0c 00 00 00   12           offset of the payload
00 00 00 00
01 09 ff fe   0xFEFF0901   D3DX9 effect magic
73 70 72 69   "spri"       <- where a real effect has the offset of its body
```

`MOJOSHADER_parseEffect` reads those last four bytes as that offset, finds `offset > len` and
reports `Unexpected EOF`. It is not a truncated effect: **XNA 4.0 on Windows Phone supported no
custom shaders at all**, so `Effect(GraphicsDevice, byte[])` could only ever name a built-in, and
this is the reference — container, magic, four-character tag. In the game's PE the tag is followed
by a length-prefixed `"Windows Phone.v4.0R1.Reach"` and then the obfuscator's encrypted blob, with
no shader bytecode anywhere near it.

`Microsoft.Xna.Framework.Graphics.StockEffectStub` recognises that shape and substitutes the bytes
WPR already ships in `Resources`. It identifies a reference **positively** — container magic, then
D3DX magic at the stated payload offset, then four bytes that are ASCII letters *and* do not
address a body inside the blob — so a genuine XNA4-wrapped effect returns null and compiles exactly
as before. **Only `spri` is attested**; the other five tags are inferred from the same four-letter
rule and simply will not match if that rule is wrong, which leaves the original error in place
rather than substituting the wrong shader.

Verified end to end on Windows after both fixes: boot, PLAY menu, CONTINUE, and a live level 1-1
board with the timer running — zero `Game.Draw threw`, zero `[wpr-ex]`, and the only first-chance
exception left is .NET's own probe for a neutral-culture satellite assembly the game does not ship.
Mirror's Edge, Angry Birds and Kinectimals still launch clean, which is what says the stub check
does not misfire on a real effect.

Shim behaviour in `WPR.Framework.Xna`, so no patcher bump and no reinstall for this half — but the
game needs the v33 repatch above before it gets far enough to matter.

### A signed-in gamer gets a token-shaped partner token (2026-10-21)

`Gamer.GetPartnerToken` returned `""` while `SignedInGamer.IsSignedInToLive` answers true. A
signed-in WP7 device always got a SAML `EncryptedAssertion` holding one xmlenc `EncryptedData`, and
the WP7 Xbox LIVE sample's extractor, copied verbatim by titles, does
`XDocument.Parse("<root>" + token + "</root>")` and `.Single()` of the `EncryptedData` descendant.
**Contract Killer** (`3a9b71b7-…`, Glu, `XNAConnect.GetToken`) has no guard. Its Xbox login runs
from `Update` once its dead server times out (~35 s, still on the title screen). It is a
variable-timestep game, so the `InvalidOperationException` ended the run (hub report
`update-threw … ThrowNoElementsException`). Glu added `if (partnerToken.Length == 0) return "";` in
the later Gun Bros. Battleship and DracoWP catch it.

`Gamer.PlaceholderPartnerToken` is now that shape with placeholder cipher data, from both
`GetPartnerToken` and `EndGetPartnerToken`. Callers extract it, send it to the dead service and
take their own request-failed path. `PartnerTokenTests` runs the sample extractor over it.
Verified D1–D4 (harness): no throw, title → tips → first mission. The briefing's OK button never
took a harness tap; Back advances it. Most likely the harness's synthetic finger staying `Released`
in `GetState()` every later frame (unproven, and unrelated to the token). Not run on Android (no device attached).

### AE Fruit Slash plays: what a WP7 library learns about its own package, touch ids, named mutexes (patcher v58, 2026-10-23)

AE Fruit Slash (`0456cf7d-1802-4e07-8120-304a06cbdbf1`, AE Mobile's `AEMobile.WP7.GameFramework.XNA`
plus Microsoft's ad SDK and Scoreloop) started after v44 but did not really work: nothing saved,
Back reopened the screen it had just closed, and the round froze on the second slash. Five platform
fixes, none touching the game's DLLs:

| fix | without it |
| --- | --- |
| `Module.get_Name` call sites go to `WPR.WindowsCompability.ReflectionModules.GetName` (patcher v57, `RedirectModuleNameCalls`): a stream-loaded module is `"<Unknown>"` to .NET, so it answers `ScopeName`, the file name | AEMobile reads its version from the assembly whose `ManifestModule.Name` is `EntryPointAssembly + ".dll"`; with no match the version was null, `StateManager.CheckDirectory` threw on `Path.Combine(.., null)`, and no setting, score or launch count was ever saved |
| `Assembly.Load(string)` goes to `Assembly2.Load` (a `MemberPatches` entry), which hands back an assembly already loaded in any context (Type2's lookup, `Type2.FindLoaded`) before asking the runtime | a library in the Default context cannot bind into the game's collectible one (`FileLoadException 0x80131515`): the ad SDK's `AdManager..cctor` does `Assembly.Load(Deployment.Current.EntryPointAssembly)` |
| `Deployment.Current` is read from the install's `AppManifest.xaml` at launch (`Deployment.LoadForLaunch`, reset at teardown): EntryPointAssembly, EntryPointType, RuntimeVersion, Parts | it was always empty, for XNA titles too |
| the synthetic finger's slot gets `NO_FINGER` on every idle tick (`SyntheticTouchInputBackend`) | after a tap the slot reported `Released` in `GetState()` **for ever**. AE's buttons fire on a Released touch inside them, so the menu pressed the gear again the moment Back closed Settings. This is the "harness tap stays Released" seen on Cut the Rope and Contract Killer: it was the injector, so keyboard touch bindings and the mouse wheel had it too |
| `TouchPanel.SetFinger` assigns XNA ids itself: a new id for every new touch (`NewTouchId`), kept while it is down, followed when the SDL drain shifts a held finger to a lower slot (no longer reported as released there); a press in a slot that was Released last frame is `Pressed`, not `Moved` | the sources reuse ids: the mouse (`int.MaxValue`) and the synthetic finger are constants, Android pointer ids restart at 0. AE keys blade trails by id and forgets a released id in only two of its three dictionaries, so the second slash threw `ArgumentException`/`KeyNotFoundException` from every Update and the round froze, on any head and any input |

Two more were Android-only:

| fix | without it |
| --- | --- |
| named `Mutex` constructors and `Mutex.OpenExisting` go to `WPR.WindowsCompability.NamedMutex` (patcher v58, `RedirectNamedMutexCalls`): the OS mutex where there is one, else **one process-local mutex per name**, which every `Mutex` of that name waits on through a non-owning handle | **no named mutex can be created on Android.** CoreCLR keeps named objects under `TMPDIR`, the Android host leaves it unset, and the runtime fixes the directory at start-up to `/data/local/tmp`: `IOException ... mkdtemp("/data/local/tmp/.dotnet.XXXXXX") == nullptr; errno == EACCES`. Managed code cannot move it (it is chosen before managed code runs, and `AndroidEnvironment` values do not reach native `getenv`). AE died in `AEGame..ctor`; its save path takes another. Log: `[wpr-mutex] named mutexes are process-local in this process` |
| FNA3D Vulkan's `VULKAN_INTERNAL_AllocateDescriptorSets` returns 0 with one warning for `VK_ERROR_OUT_OF_POOL_MEMORY` / `VK_ERROR_FRAGMENTED_POOL` instead of `FNA3D_LogError` (`libFNA3D.so` rebuilt, all three ABIs; `FNA3D.dll` not) | AE makes a **new `BasicEffect` for every particle and blade-trail draw** (`~Effect` gives them back; live stays ~60). Every shader keeps one uniform descriptor set for life, two per effect, so the 1,024-set uniform pool filled after ~512 effects, about 40 s of hard one-finger slashing on the S24. The driver already had a grow-the-pool path, but the failed first attempt went to `FNA3D_LogError`, **whose managed hook throws**, so the process aborted (`vkAllocateDescriptorSets VK_ERROR_OUT_OF_POOL_MEMORY`, unhandled, SIGABRT) before the recovery ran. The sampler-pool halving had the same flaw. Desktop drivers here do not enforce maxSets, so D2 never showed it. `[wpr-effects] created=N live=N` (Trace) every 128 effects tells this from a leak: a flat live count with a pool failure is this |

**The tell for the id class**: an exception naming a touch id (`2147483646` is the synthetic finger,
`2147483647` the mouse) from a game's input code, on the second gesture and never the first.
The source ids still reach `GestureDetector` (`INTERNAL_onTouchEvent`) unchanged; only `TouchLocation.Id` changed.

**The harness `peek` verb** (local `scratchpad/gamerun/Script.cs`) reads a static member path:
`peek AEMobile.WP7.GameFramework.XNA.ScreenManager Current.sequeceScreens` lists AE's screen stack
(an element list prints each element's type). That is what showed Settings being closed by Back and
re-added the next frame.

What remains, all the game's own network or a launcher with nothing behind it: every AE, ad and
Scoreloop host is dead (`lemur/ant/monkey.ae-mobile.com`, `mobileads.msn.com`), so the round-end
"Guess you like" panel spins until its X is tapped, and the ad SDK's `AdComponentCommon.Update`
throws "Collection was modified" when a failed ad request removes the ad mid-enumeration (WPR's
component loop survives it). Rate Us works once per launch: the game waits for the
Deactivated/Activated a Marketplace launch caused, and `MarketplaceReviewTask.Show()` is a no-op.
`Launching`/`Activated` NRE in the pre-Run priming pass (the game's `GameService` is created in
`Initialize`); the post-first-tick pass succeeds. There is no leaderboard or achievement UI.

Verified (desktop harness, scratch data root): menu, Help, Settings (sound/music off and on, kept
across a relaunch), Rate prompt on the fifth launch, slash to start, fruit cut and score rising,
BEST kept across a relaunch, bombs, pause and resume (button and Back), Back in play to pause, game
over, Back to the menu, Back on the menu to the Quit dialog.

| cell | result |
| --- | --- |
| D1 D3D11 | ✅ menu → play → pause → resume → game over → menu |
| D2 Vulkan | ✅ same; heavy slashing (~30 effects/s) keeps ~60 native effects live |
| D3 OpenGL | ✅ same (loads ~8 s slower) |
| D4 GLES 3.0 + no DXT | ✅ same |
| A1 emulator GLES 3.1 | ✅ menu, pause/resume, play, game over, Back to menu ↔ Quit |
| A2 emulator Vulkan | ✅ same |
| P1 S24 Vulkan | ✅ user-tested after the driver fix: over 4,000 effects created in one session (live 60-116), no crash. Before it: aborted on the second long slash |
| P2 S24 GLES 3.2 | not run (phone reserved) |
| P3 Mali | unverified, no device |

A held Back is Android's force-quit (`GameActivity.OnKeyLongPress`). On the GLES emulator an injected
`input keyevent KEYCODE_BACK` can arrive as a long press and close the game, which looks like a
crash in a scripted run and is not one.

Regression of the touch change, base `0bfacad2` vs this branch, same scripts (D1): Angry Birds,
Cut the Rope, Fruit Ninja (sliced into a game, score rising on both), Carcassonne, Flowerz,
Minesweeper (panorama swipe) reach the same screens. Unit tests: `SyntheticTouchTests` (a tap is
Released for one frame, a press after a release is Pressed with a new id, a shifted finger keeps
its id) and `ReflectionAndDeploymentTests`.

**Patcher table changes (v57, v58)**: `--repatch-installed` on the desktop, automatic on Android.
Not identity-binding. The touch and Deployment changes are shim behaviour and need no repatch;
the driver fix is a native binary, so Android needs the APK repackaged.

### iRunner plays: the store API, settings that hold a game's class, a game-thread context (patcher v59, 2026-10-23)

iRunner (`525de61b-…`, Droidhen, cocos2d-xna) bundles Umeng analytics, OpenXLive, Microsoft's ad
SDK and a `StoreWrapper.dll` compiled against Windows.winmd. The library's first copy was
PlayReady-wrapped (`NotDecrypted`); a decrypted XAP replaced it. Seven platform fixes, none in
the game's DLLs:

| fix | without it |
| --- | --- |
| `Windows.ApplicationModel.Store` in `WPR.Framework.WinRT` (`CurrentApp`, `LicenseInformation`, `ListingInformation`, `ProductLicense`, `ProductListing`, `ProductType`), answering as the WP8 native runtime does: a purchased copy, a store with nothing for sale; purchases and receipts fail with `0x803F6107` | `LoadListingInformationAsync` was missing, `applicationDidFinishLaunching` aborted, nothing drew |
| `IsolatedStorageSettings2` reads with `LoadedTypeContractResolver`, which finds a data contract's type in any loaded assembly (game contexts first) | `Save()` wrote game classes with known types and the read had none: every setting holding one (iRunner's whole save, `Classes.Model.AppSetting`) was dropped on each launch |
| the game thread of a managed title has a `SynchronizationContext` (`WPR.Xna.Rhi.GameThreadSynchronizationContext`, posts through `WprGameThread`), installed and restored by `ApplicationLaunch` | OpenXLive's `NavigationService.Initialize` throws "Invalid cross-thread access" without one; its pages never opened |
| `Deployment.Parts` is Silverlight's `AssemblyPartCollection` of `AssemblyPart` (`string Source`) | it was `DeploymentPartCollection`, a name no game binds; OpenXLive's `ManifestParser` died on the `TypeLoadException` |
| `GameComponent.Dispose()` returns when re-entered on the same component | the ad SDK's `AdComponentCommon.Dispose` calls `Dispose()` on the component disposing it; `Game.Dispose` at teardown overflowed the stack and took **the whole desktop launcher** down. On the phone the shell ended the process without disposing |
| Back is hidden from the second and later fixed-timestep Updates of a tick (`WprPhoneBackButton.HiddenFromCatchUpUpdates`; Draw still sees it) | Back is polled once per tick, so a catch-up tick read one press as several: OpenXLive closed its page and the game then quit |
| `GameLifecycleQuirks.SingleColdStartProducts`: no pre-Run priming and no boot `Activated`, i.e. one `Launching` after the first tick | Umeng's `onLaunching` (on Launching and Activated) ran four times; its session thread copied a session the next call was rebuilding, and `string.Copy(null)` killed the process in about one cold start in four |

**The context changes what every managed XNA title sees** (`SynchronizationContext.Current` was
null on its game thread). WPR code that blocks the game thread on a task must keep `await`s inside
it off this context (`ConfigureAwait(false)` or `Task.Run`), the trap the Silverlight host already
has. Audited when it went in: GamerServices, `LeaderboardReader`, the hub client, `PlaytimeTracker`
and the online store are clean. The WP8 native runtime gets no context (its Xbox Live host waits on
hub tasks from the guest thread).

Left alone, all the game's own: Umeng parses an invariant-format date with the current culture
(`FormatException`, caught, on any non-US locale); OpenXLive's Score button and Umeng/ad requests
go to dead servers.

GPU matrix (menu → Adventure → boosts → run with Jump/Slide → death → Mission Fail → Back →
achievements → Back → Quit → Exit):

| cell | result |
| --- | --- |
| D1 D3D11 | ✅ full flow, 5/5 fresh-store runs clean |
| D2 Vulkan | ✅ |
| D3 OpenGL | ✅ |
| D4 GLES 3.0 + no DXT | ✅ (washed out, as D4 always is) |
| A1 emulator GLES 3.1 | ✅ full flow; settings survive a relaunch |
| A2 emulator Vulkan | ✅ |
| P1/P2 S24 | not run (the phone is not for test installs) |
| P3 Mali | unverified, no device |

Regression (D1, 30 s, tap and Back): Angry Birds, Cut the Rope, Doodle God, Chickens Can't Fly,
Battlewagon, Feed Me Oil, Carcassonne, Flowerz, AE Fruit Slash run, with only their known errors.
Battlewagon exited 127 once in 12 runs, at teardown after its own Back-quit; not reproduced in 11
more (6 later, 3 first-run) or in 7 baseline runs. Tests: `SettingsContractResolverTests`,
`GameThreadContextTests`, `GameComponentDisposeTests`.

**Patcher table change (v59)**: the store types and `AssemblyPart` rescope by name. Not
identity-binding: `--repatch-installed` on the desktop, automatic on Android.

### An empty gamertag is not the same as no gamertag (2026-09-19)

`Gamer`'s ctor read `Configuration.Current.GamerTag ?? "HarryDirk"`. The **Android head writes
`"GamerTag":""` into config.json while the Windows head omits the key entirely**, so `??` fired on
one platform and not the other, and Android games saw a **zero-length** gamertag.

**Brain Challenge turns that into an `IndexOutOfRangeException` on every frame of `Draw`.** It does
`ay.q[0] = v.a(gamer.Gamertag)` and then indexes that byte[] — so an empty tag is not a blank name,
it is an out-of-range read. The menu still drew (the throw happens partway through the draw list),
the date in the header simply never appeared, and `Game.Draw threw` fired ~60 times a second.

Two things worth keeping from how this was found:

- **It only surfaced once the Mono fix above was in.** Before that, `Update` was dead from the
  InvalidProgramException and the buffer was never reached — a fixed bug revealing the next one,
  again.
- **The discriminator was running the SAME patched DLL on both heads.** Identical IL, identical
  content-load sequence down to the last `196.xnb`, Windows clean and Android throwing thousands.
  That is what ruled the IL transform out and pointed at host configuration. When a defect appears
  on one head only, **run the other head's bytes on it before suspecting your own change.**
  Be careful to actually reproduce the same path: the first Windows attempt silently reused an
  existing profile and skipped the whole first-run flow, which made it look clean for the wrong
  reason. WPR's Windows saves live under `%LOCALAPPDATA%\IsolatedStorage\...\AppFiles`, **not**
  under `%LOCALAPPDATA%\WPR`, and the store folders are not per game.

Normalised in one place (`Gamer.Normalise`) and applied at both the ctor **and** the `Gamertag`
setter — the Windows settings page assigns straight from a text box, so clearing it would otherwise
reintroduce the empty tag at runtime. XNA has no concept of a signed-in gamer without a tag, so this
is what the API already guarantees.

No `ApplicationPatcher.Version` bump and no reinstall — shim behaviour in `WPR.Framework.Xna`.

### The mouse wheel now scrolls, because a short mouse DRAG is a tap (2026-09-19)

Reported as "when I am scrolling, if in the middle, it jumps the menu" — dragging Chickens Can't
Fly's laboratory list opens whichever lab was under the cursor instead of scrolling.

**The touch pipeline was not at fault, and that was measured rather than assumed.** A click with 13
px of movement delivered exactly 13 px to the game, and one continuous 380 px drag arrived as
**exactly one** Pressed/Released pair — no inflation, no fragmentation. What bites is the game's own
recogniser (`Evozon.Games.Common.Input.GestureDetectors`): `TapDetector(1 s, 40 px)` calls anything
under **40 px** of travel a tap, while `DragDetector(15 px)` starts a drag at **15**. Between 15 and
40 both fire, and the tap wins. A finger never lands in that window because a flick travels hundreds
of pixels; a mouse hand lands in it every time.

**So the fix is to remove the need to drag at all: the wheel now scrolls.** `WheelTouchScroll`
(`Src/Core/WPR.Framework.Xna/Backend/`) turns notches into a synthesised vertical finger drag,
delivered through the existing `SyntheticTouchInputBackend`. Four things about it are load-bearing:

- **80 display px per notch, deliberately well above any plausible tap tolerance.** A short
  synthetic drag would land in the very 15–40 px window this exists to escape, and the wheel would
  open labs too.
- **Consecutive notches coalesce into one drag**, lifting only after ~6 idle frames. A
  press/release per notch would be short enough to read as a tap again, and would hand the game a
  burst of flicks with their own inertia.
- **Spread over 4 frames per notch**, because a one-frame teleport is a single enormous delta that
  flick detectors read as a violent throw.
- **Mutually exclusive with the existing wheel→Pinch synthesis**, which only fires when a title
  enables Pinch. Otherwise one notch would zoom *and* scroll.

**Why a gesture would not have worked here.** The obvious implementation is `EnqueueGesture`, and it
is useless for this game: a Cecil scan shows it sets `EnabledGestures` and then **never calls
`ReadGesture`** — it scrolls off raw `TouchPanel.GetState()` positions. WP7 lists routinely predate
trusting the recogniser, so **check whether a title reads gestures at all before synthesising one.**

**A real bug in the injector fell out of this, and it affected the keyboard→touch path too.**
`SyntheticTouchInputBackend` wrote `SetFinger(slot, SyntheticFingerId, …)` on a `JustReleased`
sample, i.e. the finger stayed present in `GetState()`. Nothing else ever clears that slot — the
platform drain is explicitly told to skip it — so the synthetic finger was **down for ever** after
the first gesture. The symptom is a game that behaves as though you never let go: the lab list
stayed over-scrolled past its top (a black band above the header) instead of springing back. A
`JustReleased` sample now writes `NO_FINGER`, which `SetFinger` turns into a Released at the
previous position, while the gesture channel still gets its `INTERNAL_onTouchEvent(Released)`.

**Two diagnosis traps worth remembering**, both of which cost real time here:

- **The touch traces are capped** — `GetState` 30, `INTERNAL_onTouchEvent` 30, `SetFinger` 30, ticks
  30. Traces stopping does **not** mean input stopped. Raise the cap in a local build to measure a
  gesture; do not infer from silence.
- **`keybd_event` with a scancode of 0 is silently dropped by SDL.** Escape looked like it did
  nothing — "Back is broken in this game" — until it was sent with `MapVirtualKey(vk, 0)`. The
  `[wpr-input] Back asserted from key …` line is the check: no line means the key never arrived, not
  that the game ignored it.

Verified in the real launcher: wheel down and up over the lab list scrolls smoothly, clamps at both
ends, springs back, never opens a lab — and a click still selects Hatchery immediately afterwards.
Desktop only in effect (Android registers no keyboard-emulation host, so the injector is not
attached, and a phone has no wheel), but the code is platform-neutral and both heads build it.

No `ApplicationPatcher.Version` bump and no reinstall.

### A suppressed draw starves FNA3D's off-thread command queue (2026-09-01)

> **Neither head runs this driver by default any more** (Windows is D3D11, Android moved to Vulkan
> on 2026-09-07), so nothing below is reachable unless a device is put back on OpenGL through
> `fna3d_driver.txt`. The mitigation stays: it is cheap, it is correct on every driver, and the
> override exists precisely so a phone with a bad Vulkan driver can take the GL path. Note this is
> the *same* queue described in reason 1 of the graphics section above — this section narrows it to
> the `SuppressDraw` case, and the driver switch is what removed the general one.

`Game.SuppressDraw()` used to skip `BeginDraw`/`Draw`/`EndDraw` entirely, so the frame produced no
`SDL_GL_SwapWindow`. On the **OpenGL** driver that is a deadlock, not an optimisation.

FNA3D's OpenGL driver may only touch GL from the thread that created the context. Every resource
call made from any other thread is appended to a command list and the caller **blocks on a
semaphore** — `ForceToMainThread` in `FNA3D_Driver_OpenGL.c`, on 18 entry points (the
`CreateTexture*` / `SetTextureData*` / `GetTextureData*` / `Gen*Buffer` / `Set*BufferData` /
`Get*BufferData` / `GenColorRenderbuffer` / `GenDepthStencilRenderbuffer` / `CreateEffect` /
`CloneEffect` set). That list is drained in exactly one place: `ExecuteCommands`, called from
`OPENGL_SwapBuffers`. **No swap, no drain.** D3D11 and Vulkan have no such queue — they take
off-thread calls directly — which is why this only ever bit the OpenGL driver.

So a game that loads content on a worker thread behind a screen that suppresses its draws hangs
outright: the worker waits for a swap the loop will never perform, and the loop keeps suppressing
because the worker never produces anything new to draw.

**The reference case is Game Room: Pitfall!** (`{55ebed63-de3d-e011-854c-00237de2db9e}`). It stops
dead on the *second* splash logo (Krome Studios) — reported as an Android bug, reproduced
identically on Windows with `FNA3D_FORCE_DRIVER=OpenGL`. The exact shape is worth knowing because
it is what makes the symptom "second splash" rather than "first":

- `Krome.GameRoom.App.LoadContent` starts a plain `new Thread(h)` that loads every font, texture
  and sound; `App.Update` polls `k.Join(0)` and only transitions to the menu once it finishes.
- `Splash.Update` advances on wall-clock (`DateTime.Now.AddSeconds(5)`), so the **first** logo
  still times out and dequeues the second — and setting `Context.Screens.Dirty = true` for that
  swap is what lets one queued GL command through, which is the only progress the loader ever
  makes.
- When the second logo's 5 s elapse there is nothing left to dequeue, and the Loading screen it
  would transition to is created *by the loader thread* (`App.h`), which is still parked. `Dirty`
  stays false, `SuppressDraw` fires every frame, and the game sits there for ever.

A `dotnet-stack report` on the hung process is unambiguous: the loader thread sits in
`[Native Frames]` under `FnaGraphicsBackend.SetTextureData2D` ← `Texture2DReader.Read` ←
`Krome.Graphics.Font..ctor(ContentManager, "Fonts/Title")`, and the per-game
`wpr_game_debug.log` stops at `GraphicsDevice.Present #3` while the accelerometer timer keeps
ticking for ever.

**The fix is `WPR.Xna.Rhi.OffThreadGpuCalls`** (`Src/Core/WPR.Framework.Xna/Backend/`): an
`Interlocked` count of GPU calls in flight on a non-device thread, bracketed around exactly those
18 members in `FnaGraphicsBackend`, with the device thread latched in `CreateDevice` (the same call
that makes FNA3D latch its own `renderer->threadID`). `Game.Tick` then presents — `BeginDraw()` /
`EndDraw()`, no `Draw` — on a suppressed frame whenever the count is non-zero. Three things about
it:

- **The `AddDispose*` members are deliberately not bracketed.** Off-thread they append to a dispose
  list and return; they never wait. Same for `SetTextureDataYUV` and `GetTextureData3D`, which have
  no `ForceToMainThread` path at all.
- **It does not make off-thread loading fast, only finite.** One swap drains one blocked worker's
  one queued command, so a worker issuing N deferred calls serially still costs ~N frames. That is
  inherent to FNA3D's design, not to this fix.
- **A game whose game thread blocks on the worker is still a deadlock** — nothing pumps if `Tick`
  isn't running. `Krome.GameRoom.App` does exactly this in its exit path (`k.Join()` with no
  timeout), so it is reachable in principle; it needs a real fix in FNA3D to close properly.

No `ApplicationPatcher.Version` bump and no reinstall — this is loop and backend behaviour, so
games pick it up on next launch.

### Windows had silently fallen off D3D11 onto OpenGL (2026-09-01)

Found while chasing the above, and the reason it was reachable on the desktop head at all:
`SDL2_FNAPlatform.PrepareWindowAttributesWithFallback` treated a `0` return from
`FNA3D_PrepareWindowAttributes` as "this driver declined". **D3D11 succeeds with zero window
flags** — `D3D11_PrepareWindowAttributes` returns 1 having left `*flags` untouched ("No window
flags required", `FNA3D_Driver_D3D11.c`), because unlike GL and Vulkan it needs no SDL window flag
at all. So from the day the fallback ladder was introduced (2026-08-31), `(automatic)` — which on
Windows *is* D3D11, first in FNA3D's `drivers[]` — was read as a failure and every desktop launch
fell through to OpenGL. The ladder's own comment asserting that "Windows selects it automatically
and never reaches this ladder" was the assumption that hid it.

Two consequences while it was live: desktop inherited every OpenGL-only defect, this deadlock
included; and the GL path picked the **Intel iGPU** where D3D11 picks the discrete GPU
(`D3D11 Adapter: NVIDIA GeForce RTX 5090 Laptop GPU` after the fix, `OpenGL Renderer: Intel(R)
Graphics` before it).

The exemption is now `attributes == 0 && !string.IsNullOrEmpty(candidate)` — a *named* driver that
succeeds always sets `SDL_WINDOW_OPENGL` or `SDL_WINDOW_VULKAN`, so zero from one of those is still
a decline, but automatic is trusted. A genuine "nothing works" is unaffected: FNA3D logs
`FNA3D_LogError("No supported FNA3D driver found!")`, which arrives as the managed throw the ladder
already catches.

**Check the driver in any launch log before blaming a renderer bug on a game.** Desktop prints
`FNA3D Driver: D3D11` + `D3D11 Adapter: …`; anything else on Windows means this regressed again.

### `LoadFromStream` has no cache, and MonoVM asks twice (2026-09-01)

`ApplicationLaunch`'s two `Resolving` handlers used to load unconditionally, and
`AssemblyLoadContext.LoadFromStream` mints a **brand-new assembly identity every call** — there is
no path cache the way `LoadFromAssemblyPath` has one. So the moment the runtime asks for the same
game assembly twice, the context ends up holding two copies of it, and their types are not the same
type. What surfaces is an `InvalidCastException` from a cast the source says cannot fail.

**The runtime asks twice on Android and once on Windows**, which is the whole reason this was a
platform bug. XNB content names a custom content-type reader with a **partial** assembly name —
`"resgen, Version=1.0.0.0, Culture=neutral"`, no `PublicKeyToken`. CoreCLR satisfies that from the
assembly already bound in the context; **MonoVM, which is what `net8.0-android` runs on**, treats
the absent token as a different identity and raises `Resolving` again.

**Guitar Hero 5 is the reference case** (`{d289b7d1-60d9-df11-a844-00237de2db9e}`, Glu). Its entire
resource bundle — every string, texture reference and screen layout — is one
`Content.Load<com.glu.resgen_content.resgen>("resource")`. That load threw
`ContentLoadException: Specified cast is not valid`, `SG_Home.Init()` then NRE'd on the null bundle
in `CArrayInputStream.Close()`, and the game drew nothing at all: **a black screen from the first
frame, on Android only, with no crash and nothing in logcat**. The tell in the per-game log is two
`[wpr-resolve-default] OK resgen …` lines whose *requested* names differ only by the missing
`PublicKeyToken=null` — Windows logs exactly one.

The fix is `TryReuseLoadedAssembly`: every `Resolving` handler now hands back the assembly already
loaded in the target context before considering a load. Name, version and culture are all compared
— culture because satellite resource assemblies share a simple name across cultures, version so a
genuinely different build of a sibling still loads rather than aliasing.

Two things to keep in mind if you touch this:

- **Any new `Resolving` handler must do the same.** The trap is `LoadFromStream`, and that is not
  going away: it is there so the `.dll` is not locked on disk (the Repatch button depends on it).
- **Two different games shipping a same-named, same-versioned sibling do alias** in the Default
  ALC. They already did — the runtime's own binder never raises `Resolving` for a name that context
  has bound before — so this only makes our handler agree with the runtime.

No `ApplicationPatcher.Version` bump and no reinstall: this is host behaviour in `WPR.Backend.FNA`,
so games pick it up on next launch.

**GH5 is unusually intolerant of a missing audio device**, which is worth knowing when re-testing
it. It sets `SoundEffect.MasterVolume` from `CGameApp.HandleEvent` on the *first tick* and loads
`SoundEffect`s during its own init, so if the audio seam is unfilled the resulting
`NoAudioHardwareException` comes out of `Game.Update` every frame and the game again renders
nothing — the same black screen, a completely different cause. That is not hypothetical: it is
exactly what the unfilled seams below produced, and it masked this fix until they were composed.
Most titles merely lose their sound (Bejeweled LIVE logs one `NoAudioHardwareException` and carries
on to its menu). Check `[wpr-content] Load<…SoundEffect>(…) threw` in the per-game log before
concluding the display bug is back.

### Never call anything with side effects inside a `?.` argument (2026-09-01)

`FnaGameHost.RunAsync` composed the audio stack like this:

```csharp
FNALoggerEXT.LogInfo?.Invoke("[wpr-audio] " + AudioBackendRegistry.Compose());
```

`?.` short-circuits the **whole invocation expression, arguments included**. `FNALoggerEXT.LogInfo`
is filled in by `FNAPlatform`'s static constructor, and nothing has touched `FNAPlatform` at that
point in the launch — so it is null, and **`Compose()` never ran**. Every audio seam
(`IAudioBackend` / `IXactBackend` / `IMediaBackend`) stayed empty on **both** heads.

The only symptom was `NoAudioHardwareException` out of `Game.Update`, carrying FAudio's default
message — *"External component has thrown an exception"* — which is also exactly what a machine
with no sound card produces. That sent the diagnosis to the audio device and the emulator's audio
backend for a long time; the device was never opened at all, because
`FAudioContext.Create()` returns early on `!XnaBackend.HasAudio` before touching FAudio.

`Compose()` now runs on its own line. Two things make the next one of these cheaper to find:

- **`AudioBackendRegistry.LastComposition`** holds the summary, and `ApplicationLaunch` writes it to
  the per-game log right after the trace listener is attached (it cannot be logged where it is
  produced — that happens before the log file exists). One line, every launch:
  `[wpr-audio] sound=FAudio xact=FAudio media=AndroidMediaPlayer`. Read it rather than inferring
  the wiring from which projects are referenced.
- `sound=none` means composition, not hardware. That is a different bug from a device that fails to
  open, and the two are otherwise indistinguishable from inside a game.

**The composed stack, verified on both heads** (emulator, Debug APK, 2026-09-01):

| seam | Windows | Android |
| --- | --- | --- |
| sound effects (`IAudioBackend`) | FAudio | **FAudio** |
| XACT (`IXactBackend`) | FAudio | **FAudio** |
| songs + video (`IMediaBackend`) | FAudio | **AndroidMediaPlayer** (video forwarded back to FAudio's Theorafile) |

Android gets that split because `AndroidMediaPlayerModule.CreateMedia` is the only factory it
overrides — sound effects and XACT fall through to the base module untouched, which is the whole
point of the factories taking the module below rather than being parameterless. The head registers
it from `ServicesSetup.Start()`, which `GameActivity.OnCreate` runs in the `:game` process *before*
`SDLMain`, so the module is always in the registry by the time the host composes.

### Platforms declare capabilities; the engine composes (2026-09-01, Stage 6)

A platform head no longer pokes registries. It implements one `WPR.Engine.PlatformDescriptor`
saying what its device **has**, and `PlatformComposition.Apply(...)` turns that into registry
writes. `WindowsPlatform.cs` and `AndroidPlatform.cs` are meant to be **read side by side** — the
differences between those two files are the differences between the two platforms.

Before this, each head filled seven registries by hand across five assemblies with three different
lifetimes (`XnaBackend`'s twelve slots, `SensorBackend`, `AudioTranscoderBackend`,
`AudioBackendRegistry`, `SilverlightBackend`, the graphics driver lever, `NativeUI.NotificationManager`)
— and the two `ServicesSetup.cs` files were duplicated code kept in sync by hand.

**Three rules, each of which was a bug waiting to happen:**

- **Declare answers, not policies.** Emulator detection reads `Android.OS.Build`, so it stays in the
  head (`AndroidDeviceKind.IsEmulator()`) and only the *result* is declared. The engine holds no
  per-platform conditionals. Anything needing a platform API belongs on the head's side of the line.
- **`GraphicsDriver.Unspecified` ≠ `GraphicsDriver.Automatic`.** `Unspecified` leaves the FNA3D
  driver lever **untouched** — what the desktop wants (D3D11 is picked automatically and the hint is
  never set) and what keeps Android's `fna3d.env` force in place. `Automatic` actively *clears* a
  force. Conflating them silently changes desktop behaviour. Windows therefore declares no driver
  at all.
- **Composition is two-phase and idempotent.** `Apply` records the entire declaration before writing
  any registry, so a descriptor that throws half-way leaves *nothing* registered rather than a
  half-configured platform. It must be idempotent because Android runs `ServicesSetup.Start()` again
  in `GameActivity`'s `:game` process — every registry underneath is set-by-assignment, and the
  audio module stack de-duplicates by name.

**Read the platform out of the launch log.** One line per composition:

```
[wpr-platform] Android: accelerometer=AndroidAccelerometerProvider driver=Vulkan audio=[AndroidMediaPlayer] transcoder=RemoteAudioTranscoder achievements=EfAchievementStore notifications=AndroidNotificationManager tilt=none
```

That is the first thing to check for any "works on one platform" report — it says how the device was
actually set up, replacing a grep through six subsystems.

**The tier sits ABOVE the frameworks, not below.** Four of the seven RHI seams cannot leave
`WPR.Framework.Xna` — `IGraphicsBackend` speaks `Texture2D`/`GraphicsDevice`, `IAudioBackend` speaks
`Microphone`/`Vector3`, `IInputBackend` speaks `GamePadState`, `IPlatformBackend` speaks
`GameWindow` — all game-facing identities the patcher rescopes, all consumed by the framework's own
types. So the seams stay put and the engine owns composition instead. `WPR.Engine.Audio` therefore
*references* `WPR.Framework.Xna`, which is the reverse of the direction the migration plan's graph
drew, and is correct.

**Adding a capability:** add the method to `IPlatformCapabilities`, record it in
`PlatformComposition.Recorder`, write it in `Commit()`, add it to `Summarise()`, and declare it in
whichever heads have it. A capability no head declares is simply absent — absent means "this
platform does not have it", never an error.

`ContentPaths` (2026-09-07) is the worked example of that recipe, and of the "declare answers, not
policies" rule: the head states whether `\` is a path separator on its filesystem, and
`WPR.Engine.Content.ContentPaths` decides what to do about it. It is also the one capability whose
absence is *measured* rather than treated as "not present" — with no declaration the engine probes
the running filesystem, which is what keeps the bare game-host harness correct. See the patcher v23
section for what it fixes.

**Not everything is a capability.** (`SilverlightBackend.SurfaceRenderer` was the example here: a
direct registration in the Windows head, retired with `WPR.Backend.Direct3D11` on 2026-10-12.) The
per-launch RHI seams are filled by `FnaGameHost`, not from here, because their lifetime
is the game run rather than the process.


**Namespaces after the engine split.** `WPR.Engine.Audio` declares its own types in
`WPR.Engine.Audio` — `AudioBackendRegistry`, `IAudioModule`/`AudioModule` (which were
`WPR.Xna.Rhi`) and `AudioTranscoderBackend` (which was `WPR.Core`). The three *seams* they compose
stay in `WPR.Xna.Rhi` inside `WPR.Framework.Xna`, so a module implementation needs both usings.
That division is the useful one to remember: **`WPR.Xna.Rhi` is seams, `WPR.Engine.*` is
composition.**

**What stayed in `WPR.Xna.Rhi` and why**, since two of them look like engine material and are not:

- `XnaBackend.Achievements` — the framework's own `Gamer`/`SignedInGamer` read it, and
  `IAchievementStore`'s vocabulary is the game-facing `Achievement`, so it lives in the framework.
  An engine-side registry would need the framework for the interface while the framework needed the
  engine for the registry: a cycle. Same argument applies to `NativeUI.NotificationManager`.
  `WPR.Engine` referencing `WPR.Framework.Xna` and `WPR.Common` is therefore structural, not a
  leftover to chase.
- `OffThreadGpuCalls` — a coordination primitive between `Game.Tick` and the graphics backend, not
  a platform choice.
- `XnaRetainedState` — an ALC-leak diagnostic that reads framework statics; only the host calls it.
### The launch sequence and input emulation are engine code, not FNA code (2026-09-20)

`WPR.Backend.FNA` is now the FNA **adapter** and nothing else: the four RHI seam implementations,
`FnaGameHost`, the SDL-level helpers (window icon, driver hint, leaked-window diagnostics, the
Vulkan format query) and the two `Compat/` types. Two things left it:

| moved | from | to |
| --- | --- | --- |
| `ApplicationLaunch` + `GameLifecycleQuirks` | `Backends/WPR.Backend.FNA/` | `Engine/WPR.Engine.GameLoop/`, beside `IGameHost` |
| `KeyboardEmulation`, `TiltInputXnaComponent`, `TiltOverlayXnaComponent`, `SyntheticTouchInputBackend` | `Backends/WPR.Backend.FNA/Input/` | `Engine/WPR.Engine.Input/` (new project) |

**Why this is only possible now.** Both were pinned to the backend by one fact — the spine types
were FNA's — and that fact went away with the spine relocation (step 2/3 below): `Game`,
`GameComponent`, `DrawableGameComponent` and `GraphicsDeviceManager` are `WPR.Framework.Xna`'s,
and windowing goes through `IPlatformBackend`. Measured afterwards, `ApplicationLaunch` named FNA in
exactly **four** places and the input files in **none**. Every earlier note in this file saying these
"have to live in a backend because they derive from spine types" was true when written and is
superseded. **The general rule it leaves:** when a file sits in a backend, re-check what it actually
names after every spine/seam move — the reason it landed there decays.

**The four FNA lines became `WPR.Engine.GameLoop.IGameLaunchHooks`**, implemented privately by
`FnaGameHost` and called at fixed points inside `ApplicationLaunch.Start`: `SetTitleLocation`
(`FNAPlatform.TitleLocation`), `RecordLaunchBaseline` / `HasLiveGameWindow` / `DescribePostTeardown`
(`TeardownDiagnostics`, which knows the `SDL_app` window class), `OnGameCreated` (the window icon plus
the `Compat.GraphicsDeviceManager.RequestOrientation` static) and `OnBeforeRun` (the
`PreparingDeviceSettings` → orientation wiring). The leaked-window force-dispose now goes through
`XnaBackend.Platform.DisposeWindow` — the same call, via the seam. **The teardown order inside
`Start` is unchanged**; everything else moved byte-for-byte, and the hooks shape was chosen precisely
so the sequence stays in one place rather than being re-derived by an implementation.

**Three registrations moved out of `FnaGameHost` into the launch sequence** because they name only
framework types: `SetGameThreadPost(WprGameThread.Post)`, `SetSuppressFocusActivation(...)` and the
backbuffer → `Mouse`/`TouchPanel.Display*` hook with `ResolveDisplayOrientation`. They are
registered at the top of `Start` (still before any `Game` exists); `XnaBackend.Clear()` in the
host's `finally` still drops them. The backend composes the touch decorator through
`KeyboardEmulation.WrapInput(new FnaInputBackend())`, and the launch sequence attaches the tilt
components itself via `KeyboardEmulation.AttachTo` at the point the old host lambda did.

**`Compat/GraphicsDeviceManager` and `Compat/GamerServicesComponent` deliberately did NOT move.**
Games bind both by the `WPR.Backend.FNA` assembly identity (patcher tables), so moving them is a
patcher bump plus a repatch of every install. Layout work does not justify that; they stay until
something else forces a bump.

Consequences worth knowing:

- **`WPR.Engine.GameLoop` is no longer dependency-free**, and its TFMs now mirror `WPR.Backend.FNA`
  (no plain `net8.0` leg on Windows) because it references `WPR.Database` and `WPR.Loader`. Only
  the backend referenced it, so nothing downstream changed. A backend referencing it still does not
  pull in the composition root (`WPR.Engine`).
- `WPR.Framework.Xna` grants `InternalsVisibleTo` to `WPR.Engine.GameLoop`
  (`Mouse.INTERNAL_BackBuffer*`) and `WPR.Engine.Input` (`TouchPanel`'s reserved-slot internals,
  `GameWindow.HostClientBounds`). Visibility only; the framework references nothing new.
- The backend dropped its `WPR.Framework.Silverlight`, `.Phone` and `.Devices.Sensors` references —
  every use was in the singleton reset that moved.
- `MainWindowDesktop` still calls `WPR.ApplicationLaunch.RequestExit()`; the type kept its name and
  `namespace WPR`, it just lives in a different assembly, reached transitively.
- Found on the way: `WPR.sln` carried a doubled `EndProject` after `WPR.Engine.Content` (49
  `Project(` lines, 50 `EndProject`) — Rider tolerated it, fixed anyway — and an empty
  `Engine/WPR.Engine.Input/` directory holding only an `obj/`, i.e. someone had started exactly
  this project and not finished. Both `.slnf` files list the new project.
- Verified 2026-09-20: desktop head 0 errors, Android head 0 errors (both android legs compile),
  `BackendIsolationTests` green, and Cecil shows `WPR.Engine.GameLoop` and `WPR.Engine.Input`
  reference no `FNA`.

No `ApplicationPatcher.Version` bump and no reinstall — no patcher table changed and no IL is
rewritten.

### The XNA spine is WPR-owned (2026-09-01, patcher v21)

`Game` and `GraphicsDeviceManager` no longer name `FNAPlatform`. They go through
`WPR.Xna.Rhi.IPlatformBackend`, implemented by `WPR.Backend.FNA.FnaPlatformBackend` and registered
in `FnaGameHost.RunAsync` **first**, before anything constructs a window — `Game`'s ctor calls
`CreateWindow`, so an unset slot is a launch failure rather than a late one.

This is step 1 of the spine relocation. It changes *who calls* the platform, not what the platform
does, so no patcher table changed and **no reinstall is needed**; games pick it up on next launch.

Four things that will bite if you touch this:

- **The seam names `IGameLoopHost`, not `Game`.** It has to be declared in `WPR.Framework.Xna`
  beside the other seams, and `Game` still lives in FNA — naming it would be a cycle. Measuring
  showed `SDL2_FNAPlatform` reads exactly five members off the game (`Window`, `GraphicsDevice`,
  `IsActive`, `RedrawWindow`, `RunApplication`), so an interface costs nothing.
- **`Game` implements that interface EXPLICITLY. Keep it that way.** Three of the five are
  `internal` on `Game`, and XNA 4.0's `Game.IsActive` is get-only. Games bind this type's public
  surface by identity, so making them public would change the API WP7 titles were compiled
  against. Explicit implementation reaches them without widening anything.
- **`GameWindow` lives in `WPR.Framework.Xna` now, with a `TypeForwardedTo` in FNA.** It is
  `CreateWindow`'s return type so it had to move first, and it was free to move — abstract, with no
  dependency beyond `Rectangle`/`DisplayOrientation`. `FNAWindow : GameWindow` stays in the backend
  and reaches its `internal` members through the existing `InternalsVisibleTo("FNA")`. **The
  forwarder is load-bearing**: every installed game carries IL naming
  `[FNA]Microsoft.Xna.Framework.GameWindow`, and without it they all `TypeLoadException` on
  `Game.Window`. Verify it survives a change with the exported-type table, not a green build —
  it is one attribute in `FNA.Platform/src/Properties/AssemblyInfo.cs` and nothing references it.
- **It is deliberately temporary.** Step 2 moves `Game`/`GameComponent`/`DrawableGameComponent`/
  `GameServiceContainer` up, adds them plus `GameWindow` to `ApplicationPatcher.WprFrameworkXnaTypes`,
  bumps `ApplicationPatcher.Version`, and deletes the forwarder — that step *is* reinstall-forcing.


**Step 2 landed the same day: the spine types moved up, and this one IS reinstall-forcing.**
`Game`, `GameComponent`, `DrawableGameComponent`, `GameServiceContainer`, `GameWindow`,
`GraphicsDeviceInformation` and `PreparingDeviceSettingsEventArgs` now live in
`WPR.Framework.Xna` and are rescoped there by `ApplicationPatcher.WprFrameworkXnaTypes`.
**This bumped `ApplicationPatcher.Version` to 21, and every game installed before it must be
repatched or reinstalled** (the current version is 40) —
a v20 install carries IL naming `[FNA]Microsoft.Xna.Framework.Game`, FNA no longer defines it, and
the game will TypeLoadException at launch. `--repatch-installed` is enough.

The step-1 `TypeForwardedTo` is gone; the rescope replaces it. **Do not re-add one** — a forwarder
plus a rescope gives two ways to resolve the same type, and the failure mode (a game binding the
forwarder while the patcher table says otherwise) stays invisible until a cast fails at runtime.

Only two things had to move for `Game` to become movable, which is why this was much smaller than
1,600 lines suggests: `FNAPlatform.TextInputCharacters.Length` became
`IPlatformBackend.TextInputControlCharacterCount` (it is a real platform limit — FNA's own comment
says "only 7 control keys supported at this time"), and `WprGameThread` moved with `Game` (it is
WPR-authored and never had an FNA dependency).

**The reference direction is now inverted, and that is the check.** `FNA.dll` references
`Microsoft.Xna.Framework.Game` *from* `WPR.Framework.Xna`, reaching its `internal` members
(`RunApplication`, `RedrawWindow`, the `IsActive` setter) through the pre-existing
`InternalsVisibleTo("FNA")`. If you ever see FNA *defining* a spine type again, something moved
backwards.

**Step 3 finished the job (2026-09-02): FNA now defines no XNA API at all.** Three files moved to
`WPR.Framework.Xna` — `GraphicsDeviceManager`, plus the two WPR-authored types that had been
sitting in the vendored assembly under `Microsoft.Xna.Framework` (`WprPhoneBackButton`,
`WprActivationGuard`; `WprGameThread` was already the precedent). `GraphicsDeviceManager` named
**exactly one** FNA type in 582 lines — `FNA3D_GetMaxMultiSampleCount` — and the seam member
already existed (`IGraphicsBackend.GetMaxMultiSampleCount`), so it was a one-line substitution
next to the two `XnaBackend.Platform` calls the file already made.

`FNA.dll`'s entire public surface is now `FAudio`, `Theorafile`, `SDL2.SDL`, `FNALoggerEXT` and
`FNAPlatform` — the vendored bindings plus FNA's own two extension points. **If a public
`Microsoft.Xna.Framework.*` type ever appears there again, something moved backwards.** Check with
the type table, not a green build:

```powershell
# expect exactly the five names above
$m=[Mono.Cecil.AssemblyDefinition]::ReadAssembly("...\FNA.dll")
$m.MainModule.Types | ? { $_.IsPublic } | % { $_.FullName }
```

**Not reinstall-forcing, and that was measured rather than reasoned.** All 29
`GraphicsDeviceManager` typerefs across the 22 installed games name
`WPR.Backend.FNA.Compat.GraphicsDeviceManager` scoped to `WPR.Backend.FNA` — not one names the
base — so moving the base between assemblies is invisible to game IL. `Patches` still redirects
there and `ApplicationPatcher.Version` stays 21. **Do not add `GraphicsDeviceManager` to
`WprFrameworkXnaTypes`**: that set is tested *before* `Patches`, so it would silently beat the
redirect and hand games the plain base, losing the WP7 clamp — see the comment on the set itself.

`FNAWindow` stays in the backend, but it is `internal` and no game binds it.

**What can never leave FNA**, so nobody re-opens this: `FNA3D.cs` and `FNADllMap.cs` (the map only
fires for P/Invokes whose declaring assembly is FNA), and the ~600 WPR-authored lines inside
`SDL2_FNAPlatform.cs` — mouse-as-touch synthesis, the Back-button drain, the activation guard, the
wheel→Pinch gesture, the FNA3D driver ladder. Those are inline edits to vendored event loops, not
extractable, and they are the rebase cost against upstream FNA.

**The window-compositing question does not gate any of this.** Whether the game gets its own
top-level SDL window or is composited into the Avalonia shell is answered by an *implementation* of
`IPlatformBackend` (or a different `GameWindow` subclass behind `CreateWindow`), not by the
contract. The migration plan had the two fused, which is why the spine sat blocked for a UX
decision it never depended on.

### The cold-start `Activated` is a WPR invention, and some games choke on it (2026-09-05)

Real WP7 raises `Launching` at a cold start and `Activated` only on a resume. WPR raises **both**
at boot, from `PhoneApplicationService.HandleApplicationStart(anew: true)`, because several titles
key their level/HUD setup off the activation signal and show nothing without it (Hoth,
Battlewagon — the long remarks on that method are the record). The cost is that a game which does
its own cold-start init **and** treats `Activated` as "re-initialise everything" does that work
twice.

**Doodle God** (`{34e0f2e7-…}`) dies of it. Its `Activated` handler is `DoodleGame.ᜁ()`, the full
asset + localisation init — which the game also runs itself, on its own loading thread, once its
two splash screens have shown. Each extra run re-parses `Content/data/loc/elements.txt` into the
same `Dictionary`, so `Settings.LoadElementsLoc` throws *"An item with the same key has already
been added. Key: Adventurers"* — caught on the game thread, **fatal on the loading thread**. The
process aborts about 18 frames in, on the second splash. Nothing about it is platform-specific.

`ApplicationLaunch` made it worse by firing the cold-start signal **twice**: once "primed" before
`Game.Run`, once from the post-first-tick `Activated` that `Game.Tick` synthesises. So the init
ran three times.

**The lever is `GameLifecycleQuirks`** (`Src/Engine/WPR.Engine.GameLoop/`, beside `ApplicationLaunch`
since 2026-09-20; it was `Src/Backends/WPR.Backend.FNA/`) — a ProductId table read
once per launch and passed to both cold-start call sites as
`HandleApplicationStart(true, raiseActivated: false)`. A quirked game still gets `Launching` at
boot and `Activated` on a genuine resume, which is exactly WP7's own contract. Three things about
it:

- **Do not "fix" this globally.** Dropping the boot `Activated` for everyone is the WP7-accurate
  change and it regresses Hoth and Battlewagon. A Cecil sweep of the 25 installed desktop titles
  found 3 that hook `Activated` only and 7 that hook both — and Hoth and Doodle God are *both* in
  the "both" bucket, so no property of a game's subscriptions separates the two groups. This is a
  list of names because it cannot be a rule.
- **Suppression must leave `_AppActivated` false.** That flag is what the `Activated` add accessor
  replays to a late subscriber, so setting it would re-deliver the very activation being
  suppressed.
- **The priming pass runs before `GraphicsDeviceManager.CreateDevice()`**, so any content load
  inside a `Launching`/`Activated` handler fails there with
  `ArgumentNullException (Parameter 'graphicsDevice')`, which `PhoneApplicationService` swallows.
  Doodle God takes that hit twice and survives only because its `Launching` handler just assigns
  fields; one that accumulated into a list would not. Worth remembering before blaming a game for
  a half-built scene.

Read it out of the per-game log — the two cold-start signals say so explicitly:

```
[wpr-trace] ApplicationLaunch: boot Activated suppressed for 34e0f2e7-… (GameLifecycleQuirks)
[wpr-trace] PhoneApplicationService.HandleApplicationStart(anew=True) firing Launching (preserved=true) [boot Activated suppressed for this game].
```

No `ApplicationPatcher.Version` bump and no reinstall — no patcher table changed and no IL is
rewritten. Games pick it up on next launch.

**Only the FIRST `Game.Activated` is the cold start (2026-10-03).** `ApplicationLaunch`'s
`obj.Activated` lambda called `HandleApplicationStart(anew: true)` on *every* `Game.Activated`, so
every focus regain (an Android resume, a desktop alt-tab, a `Guide` message box closing)
re-raised **`Launching`** in every game. It now does that once (for the synthetic post-first-tick
Activated) and calls `HandleApplicationStart(false)` afterwards, which raises `Activated` only,
WP7's resume signal. Doodle God was the case that found it, *after* the quirk above was in: Back on
its main menu shows a "quit?" `Guide.BeginShowMessageBox`, the dialog takes focus on Android, and
answering NO re-raised `Launching`. Its handler sets a fresh splash image, `Update` sees a splash
pending, restarts the loader thread, and `ᜁ()` dies in `LoadElementsLoc` on the duplicate
`Adventurers` key, exactly the crash the quirk was meant to stop. The tell in the session log is
`Guide.IsTrialMode read #N` straight after `Back asserted`: that read is `DoRequest`, called from
its `Launching` handler. Its `Activated` handler is guarded by `_1719 == null`, so the resume
`Activated` is a no-op once the menu exists. Backgrounding during the ~1 s splash (before the
menu exists) would still run init twice, but WP7 would do the same, and nobody has reported it.

**The pre-`Run` priming pass is skipped for a game that already owns a device (2026-10-04).**
`ApplicationLaunch` raises the cold start twice as a rule: a "priming" `HandleApplicationStart(true)`
before `Game.Run`, then again on the first synthesised `Activated`. A game that calls
`GraphicsDeviceManager.ApplyChanges()` in its constructor already has a device at the priming
point, and `Game.Run` disposes it and creates the real one (`IGraphicsDeviceManager.CreateDevice`
recreates from scratch, as XNA's did). Anything its `Launching` handler loaded is then on a freed
device, and the first draw that samples it is an **access violation in `FNA3D_VerifySampler`**,
reported as `Fatal error. 0xC0000005` with no managed exception. **AE 3D Motor** (`bb5f1317…`) is
the reference case: `Launching` pushes a `SplashScreen` that loads its textures at once. Such games
now get `Launching` once, after the first frame (`[wpr-trace] … skipping the pre-Run lifecycle
priming`). Measured over the installed library: AE 3D Motor, ilomilo, Mirror's Edge, Skulls of the
Shogun and Tower Bloxx New York create a device in a constructor; ilomilo and Skulls subscribe to
the lifecycle, harmlessly, and all five run. AE 3D Motor is also in `GameLifecycleQuirks`: its
`Activated` handler is a tombstone restore that clears the screen stack and then NREs on a saved
`VariableBank` that does not exist at a cold start, which is a white screen.

### Launching one game without the launcher UI

Driving the Avalonia list to reproduce a game bug is slow and stops working the moment the
workstation locks. A ~60-line console app that references `WPR.Backend.FNA` + `WPR.Database`, sets
`Configuration.Current`, reads the row out of `applications.db` and calls
`new FnaGameHost(app).RunAsync()` boots any installed XNA game directly. Two things it needs:

- **`FnaGameHost`, not `ApplicationLaunch.Start`.** The host is what registers the RHI backends
  (`XnaBackend.SetGraphics/SetAudio/SetInput/…`); calling `Start` directly dies on
  "No IInputBackend has been registered" the moment `Game..ctor` runs `FrameworkDispatcher.Update`.
- **Run it from the desktop head's output directory**, with its own files copied in beside
  `WPR.Platform.Windows.exe` — the native `SDL2` / `FNA3D` / `FAudio` DLLs are copied there by that
  project, not by a `ProjectReference`.

`ServicesSetup.Start()` is *not* needed (it pulls in Avalonia); a game just runs without
achievements or the keyboard tilt emulator. `FNA3D_FORCE_DRIVER=<name>` in the parent shell is
inherited by the child, which is how another head's driver gets reproduced on Windows — set it to
`Vulkan` to run what Android runs, or `OpenGL` to reach the GL-only failure modes.

### `Type.GetType` from a game resolves through the runtime's parser (2026-10-04)

`WPR.WindowsCompability.Type2.GetType`, the target of every patched `Type.GetType(string, bool)`,
split the name on commas and stepped through the pieces four at a time. A generic argument carries
its own assembly-qualified name inside `[[…]]`, so it rebuilt
``List`1[[Microsoft.Xna.Framework.Vector2, Microsoft.Xna.Framework, …]], mscorlib, …`` as
`…Vector2, FNA, mscorlib` and the runtime threw `FileLoadException: 'FNA,  mscorlib'`. **Skulls of
the Shogun** died on it loading `Content/Maps/Overworld Map/Overworld_Map.apt` (SharpSerializer).
It also mapped XNA assembly names to `FNA`, which defines no XNA type since the spine move. Now
`Type.GetType(name, assemblyResolver, typeResolver)` does the parsing: XNA names resolve to
`WPR.Framework.Xna`, others by simple name in the caller's ALC, then every ALC, then the Default
binder (which maps WP7's `mscorlib, Version=2.0.5.0`). `Type2Tests` covers the shapes. Eight
installed titles call it; all that the harness can run still start. Shim behaviour, no patcher bump.

### Content type names reach the assembly the installer renamed (2026-10-23)

The patcher renames a game assembly with punctuation in its name
(`AssemblyNameStandardization`: `*'",_&#^@!` become `_`), so Zuma's Revenge's `Zuma's Revenge!`
is installed as `Zuma_s Revenge_`. Its content is not rewritten: `Content/levels/levels.xnb` still
names `ZumasRevenge.LevelsXmlReader, Zuma's Revenge!`. The runtime's type-name parser rejects that
name before any resolver is asked (an apostrophe opens a quoted token: "The given assembly name was
invalid"), so `ContentTypeReaderManager.LoadAssetReaders` threw twice per load. The game never
noticed, because the comma-split fallback applies the same regex and found the reader, but each
throw is first-chance and reached WPR Hub as a `missing-assembly` compat group (0.2.06, Windows and
Android). `WPR.Xna.Compat.InstalledAssemblyNames.StandardiseTypeName` now rewrites only the simple
name of every assembly in the string (outer and generic arguments) before parsing; `Type2.GetType`
does the same for names a game passes in code. **Keep its character set in step with
`AssemblyNameStandardization`** (`WPR.Loader` is above `WPR.Framework.Xna`, so it is a copy).

The same loader also stopped trying a plain `Type.GetType(name)` first. For any reader in the
game's own assembly that call throws `FileLoadException` ("Operation is not supported": the Default
context refusing the collectible game assembly), first-chance again, before the resolver overload
finds it. The resolver overload now goes first, scanning loaded assemblies by simple name and
letting the Default binder load anything not yet loaded (the `mscorlib` facade).

Measured, desktop D3D11 harness, 30 s each, 0.2.07 vs this change: Zuma 2 `FileLoadException`s to
0, **Plants vs. Zombies 221 to 0** (its `Sexy.TodLib.*Reader, LAWN` readers, one throw per load).
Kinectimals, AE 3D Motor, Pitfall!, Fable: Coin Golf, ilomilo, the XLSContentLibrary title
`09cc7f9c` and Guitar Hero 5 are identical (same first-chance list, same content errors, no thrown
frames). The player sees no difference: the same reader type is found. Tests:
`InstalledAssemblyNamesTests`. No patcher bump.

### Isolated storage always opens shared (patcher v20)

Every `IsolatedStorageFile.OpenFile(…)` / `CreateFile(…)` call site in a game is rewritten by
`ApplicationPatcher.RedirectIsolatedStorageOpens` to the matching static on
`WPR.WindowsCompability.SharedIsolatedStorage`, which opens with `FileShare.ReadWrite`. The
instance becomes argument zero, so the evaluation stack is unchanged and only the callee moves.

**Why a body rewrite and not a table entry.** `MemberPatches` only swaps a member reference's
`DeclaringType`, which requires the replacement to be substitutable for the instance on the stack —
and `System.IO.IsolatedStorage.IsolatedStorageFile` is `sealed`. Nothing can stand in for it, so
`callvirt instance T Store::OpenFile(a, b)` becomes `call T Shim::OpenFile(Store, a, b)` instead.
This is the second entry in `ApplyGameSpecificFixups`' neighbourhood; unlike that one it runs over
every assembly, not a named title.

**Why `SharedIsolatedStorageFileStream` wasn't enough.** That shim has existed for a while, but it
is installed through `MemberPatches` for the two `IsolatedStorageFileStream` **constructors** — it
only covers games that `new` a stream themselves. `OpenFile` builds its stream *inside the BCL*,
IL the patcher can never reach, so games that go through the store got none of the fix.

**The failure it fixes.** On WP7 each app was its own short-lived process, so leaking an
isolated-storage handle cost nothing. WPR hosts games in one long-lived process, so a leaked handle
outlives its read and blocks the next open of the same file. Angry Birds is the reference case: its
reader `al::b` returns the bytes **without closing the stream** whenever the file is non-empty (only
the zero-length branch calls `Close()`); its writer `al::a` then opens the same path with
`FileMode.Create`, collides, swallows the `IsolatedStorageException` in its own `catch`, and falls
through to `Write` on a **null** stream. The caller's `catch (System.Object)` eats the resulting
`NullReferenceException`, so the game looks healthy and simply never saves. Measured before the fix:
12 `[wpr-fce]` lines every launch, on both `settings.lua` and `highscores.lua`. After: zero, both
files written on `GameMain::OnExiting`, and a muted-sound setting survives a restart.

**Expect this to have fixed saves in more than one game** — nothing about the leak is Angry Birds
specific, and it was free on real hardware. Worth re-testing any title reported as "loses
progress".

`[iso-fixup] redirected N … call(s)` is written to the install log per assembly, so the count says
whether a given game used this path at all.

**This was a patcher table change (v20).** The current version is **40** — see "Windows path
separators in game file I/O" above for the most recent bumps; this paragraph describes what v20
itself changed. Unlike v19 it is not identity-binding — a v19 install still launches, it
just keeps the exclusive share and keeps failing to save. `--repatch-installed` is enough (it
restores each `.dll.original` first, so repatching is idempotent).

### Every game has its own isolated store (patcher v38, 2026-09-25)

The BCL keys `IsolatedStorageFile.GetUserStoreForApplication()` on the host's **entry assembly**,
so every game WPR ever hosted shared one store, and two titles using the same filename overwrote
each other. (On Windows that store also changes with the exe's path, which is why
`%LOCALAPPDATA%\IsolatedStorage` holds a dozen `Url.*` folders: one per build or harness.)

**Gravity Guy is the reference case** (`4f930d12-…`), reported as "doesn't load any more". It,
Fragger, Monster Island and iStunt 2 all bundle Miniclip's `DLCFramework`, which persists to
`$_StatesAfterExitData_$\DLCManager`. In the other three that copy is obfuscated, so the file's root
is `<d>`/`<b>`; Gravity Guy's `XmlSerializer` throws on it inside `DLCManager.Initialize`, which
aborts `Game.Initialize` before `LoadContent`, so no Cocos2D scene is ever run. `drawScene` then
NREs per frame after `glPushMatrix`, and eleven frames later the matrix stack overflows
(`IndexOutOfRangeException` in `NSObject.glPushMatrix`). **That overflow is the loud line and it is
three layers downstream**; the first `[wpr-fce]` of the launch is the XML error. One session of
Fragger was enough to break it for good, because the framework deletes that file only after reading
it successfully.

**The mechanism: `SharedIsolatedStorage.GetUserStoreForApplication` → `PerGameIsolatedStorage`**
returns a real `IsolatedStorageFile` with its private `_rootDirectory` moved to
`<DataStore>\IsolatedStore\<ProductId>\AppFiles\`. Every BCL member, `IsolatedStorageFileStream`
included, resolves paths through that one field, so the whole surface is isolated, including the
parameterless listings the path shims deliberately skip. Checked on .NET 8 and .NET 10, kept from
the trimmer by `[DynamicDependency]`. If it is ever missing, the shared store is returned and
`[wpr-isostore]` says so, rather than throwing. The game comes from
`WprHostEnvironment.CurrentProductId`, which both launch paths set.

Four things are load-bearing:

- **The `<ProductId>\AppFiles` layout copies the BCL's for a reason:** `IsolatedStorageFile.Remove()`
  deletes the root **and then its parent** when the parent holds nothing else. Put the per-game
  folder directly under `IsolatedStore` and one game's `Remove()` would delete every game's saves.
- **`IsolatedStorageSettings2.ApplicationSettings` is rebuilt when the product id changes.** It is
  a static in a WPR assembly, not the game's ALC, so it outlived a launch. That meant cross-game
  settings before this change, and would mean writing into the previous game's store after it.
- **Migration is a manifest, `IsolatedStore\shared-store-migration.txt`**, written on the first use
  and listing every folder beside the running game's install folder, i.e. every game installed at
  the cut-over. A listed game copies the old shared pool into its store on first open (never
  overwriting) and is struck off. Games installed later are not listed and start empty, as a
  fresh WP7 install did. The old store is left untouched. **The source is the running host's BCL
  store**, so running a harness first would migrate from the harness's pool. Delete
  `IsolatedStore` to redo it.
- **`$_StatesAfterExitData_$` and `$_TombstoneData_$` are not inherited.** They hold transient
  DLCFramework pending-purchase state and are exactly what the four games collide on. Measured:
  with them copied, Fragger inherited Gravity Guy's copy and drew nothing on its first launch.

Verified with the headless harness, from a clean store: Gravity Guy → Fragger → Gravity Guy →
Fragger. Each migrated once, with zero `ArgumentNull`/`NullReference`/`IndexOutOfRange` and frames
drawing on every run. Each store holds its own `DLCManager` file in its own shape.

**The player can clear a game's data**: "Clear data" in the desktop game pane, "clear data" in the
Android long-press list, both behind a warning, both calling
`PerGameIsolatedStorage.ClearGameData(productId, installsRoot)`. It **strikes the game off the
manifest before deleting**. A game still waiting to migrate would otherwise re-inherit the old
shared pool, and with it the file that broke it, on its next launch. If no manifest exists yet it
writes one listing every *other* installed game. Achievements live in `achievements.db` and are
unaffected. The desktop refuses a game still below v38 and asks for a Repatch, because such a
game reads the shared store and a clear would look like it did nothing. Android needs no such
check: `GameLauncher.Launch` repatches before the game can open a store.

**Uninstall asks whether the saves go too**: Yes/No/Cancel on the desktop, and on Android a
two-item list followed by a second confirmation for deleting. Saves are deleted **after** the
uninstall succeeds, so a failed uninstall never costs them. Kept saves come straight back on
reinstall, because the store is keyed by ProductId and not by the install folder. Deleting at
uninstall needs no repatch check: striking the game off the manifest is enough to stop a
reinstall re-inheriting the shared pool. Before this, the Android prompt claimed uninstall removed
"everything it has saved", and it never did.

**Patcher table change (v38).** `--repatch-installed` on the desktop, automatic on next launch on
Android. Not identity-binding: a v37 install launches and simply stays on the shared store.

### A game's own pause looked like the player's (patcher v39, 2026-09-29)

The Treasures of Montezuma's music turned itself off at the start of every level. The game
keeps the setting in `MediaPlayer.Volume` and `GameData.MusicVolume`, and "music off" means
"MediaPlayer paused at volume 0". Its options dialog, which is built with the level and so is
alive before the player opens it, subscribes to `MediaPlayer.MediaStateChanged` with
`if (BackgroundMusicPlayer.IsPaused) musicSlider = 0`. That is meant to mirror the phone's own
music controls pausing the game. But every level start (and level complete, and game over)
plays a track and immediately calls `Pause()` for the jingle, resuming 5 s later. The dialog
took that pause for the player's, the slider's change callback wrote 0 to both places, and the
resume played at volume 0.

`ApplicationPatcher.ApplyMontezumaMusicSliderFixup` empties that handler. It finds it by shape,
because the names are unprintable: a method that calls `MediaPlayer.add_MediaStateChanged`, then
the `(object, EventArgs)` method it takes `ldftn` of that reads
`BackgroundMusicPlayer.get_IsPaused`. That is safe because nothing outside a game can pause WPR's
`MediaPlayer` (`GameHasControl` is always true), so the handler only ever saw the game's own pauses.

Reproduced and verified on the `WPR_Verify` emulator: set music to 0.9 in the main-menu options,
start a level, and the in-game options showed 0 before the fix and 0.9 after. Only the one method
body differs in the patched assembly's IL. The install log says
`[montezuma-fixup] emptied 1 options-dialog MediaStateChanged handler(s)`.

**Patcher table change (v39).** It rewrites game IL, so a v38 install keeps the old handler.
`--repatch-installed` on the desktop; Android repatches on next launch. Not identity-binding.

### The game loop floors `TargetElapsedTime` at one 60 Hz frame

`Game.TargetElapsedTime`'s setter (`Src/Backends/FNA.Platform/src/Game.cs`) clamps anything below
`166667` ticks up to it. Do not remove this, and do not lower the floor.

In fixed-timestep mode `Tick` assigns `gameTime.ElapsedGameTime = TargetElapsedTime` verbatim, and
WP7 ports near-universally derive their delta as `gameTime.ElapsedGameTime.Milliseconds * 0.001f` —
an **integer** millisecond read. So any target below 1 ms hands the game a delta of exactly
**zero**: nothing animates, no timer counts down, every elapsed-time state machine stops, and the
loop spins as fast as the CPU allows. The floor equals FNA's own default (60 fps) and is the
fastest frame any WP7 device could present, so a game asking for 30 fps (`333333`) or 60
(`166667`) — i.e. all of them in practice — is untouched.

**The case that found it** (2026-08-31, Angry Birds): the credits page sets
`TargetElapsedTime = TimeSpan.FromTicks(3333)` — 0.33 ms — from `bd::h` on entry, and restores the
game's normal `333333` only from its *exit* handler `bd::b`. Honouring the raw value froze the
credits mid-scroll, left the hidden golden egg's unlock animation stuck on the frame before the one
that awards it, and made the page impossible to leave — because the exit that would restore the
frame rate is itself driven by the delta that is now zero. Unrecoverable without killing the game.
Reported as an Android crash; it reproduced identically on the desktop head and was never
platform-specific.

`[wpr-trace] Game.TargetElapsedTime … clamped to 166667` is logged once per game when it fires, so
the per-game `wpr_game_debug.log` names any other title that does this.

No `ApplicationPatcher.Version` bump and no reinstall — this is loop behaviour in FNA.Platform, so
games pick it up on next launch.

### Naming and placing a service module (2026-09-01)

A **module** is a pluggable implementation of a contract the engine tier owns. They live in
`Src/Modules/<Subsystem>/` and are named:

```
WPR.<Subsystem>.<Technology>
```

| Contract in | Modules |
| --- | --- |
| `WPR.Engine.Audio` | `WPR.Audio.FAudio`, `WPR.Audio.AndroidMediaPlayer`, `WPR.Audio.AudioTrack`, `WPR.Audio.MediaCodec` |
| `WPR.Engine.Notifications` | `WPR.Notifications.WindowsToast`, `WPR.Notifications.AndroidChannel` |
| `WPR.Engine.Vibration` | `WPR.Vibration.AndroidVibrator` |

**Name the technology, not the platform.** `WPR.Audio.FAudio` runs on Windows *and* Android — a
platform-shaped name would have been a lie the day it shipped twice. `WindowsToast` and
`AndroidChannel` are fine because the toast API and notification channels genuinely are the
technology; `WPR.Notifications.Windows` would not be.

**A module references its engine subsystem and nothing else** — no head, no other module, no
framework unless native bindings force it (`WPR.Audio.FAudio` must reference FNA so `FNADllMap`
resolves the natives; that exemption is recorded in `BackendIsolationTests.AllowedReferrers`).
A head references the modules it wants and declares them through `IPlatformCapabilities`.

**Anything the module needs from the app is a constructor argument, not a reference.** Extracting
`WPR.Notifications.AndroidChannel` surfaced exactly one such tie: it named
`WPR.Platform.Android.Resource.Drawable.ic_stat_wpr` for the status-bar glyph. That is app
branding, so the head passes the resource id in. If you hit something similar, prefer injection
over a reference back to the head — a module that names a head is not a module.

**Do NOT add a `<Subsystem>Module` type by default.** `IAudioModule` exists because audio has
*three* seams and partial implementations are real (Android fills songs and forwards video); the
`next` argument is for exactly that. Notifications and sensors have one interface each, so the
implementation type *is* the plug and a wrapper would be ceremony. Add one only when a module can
fill part of a subsystem.

**When adding a module:** put it in `Src/Modules/<Subsystem>/`, add it to `WPR.sln`'s **Modules**
folder and to the relevant `.slnf` (android-only projects go in `WPR.Android.slnf` only —
`Directory.Build.targets` never strips a singular `<TargetFramework>`, so the filter is the
exclusion mechanism). `BackendIsolationTests` already scans `Modules/`, so a new one is guarded
automatically.

### Audio is one project: `WPR.Engine.Audio` (2026-09-01)

**Seams, registry and composition all live there.** The only split is which *implementation* fills
it — `WPR.Audio.FAudio` and `WPR.Audio.AndroidMediaPlayer`.

```
WPR.Engine.Audio
├── IAudioBackend / IXactBackend / IMediaBackend   the three seams
├── IAudioTranscoder                               install-time transcoding contract
├── IAudioModule / AudioModule                     what an implementation plugs in as
├── AudioBackendRegistry                           composition + the composed Sound/Xact/Media slots
└── AudioTranscoderBackend                         the transcoder registry
```

**The dependency runs `WPR.Framework.Xna` → `WPR.Engine.Audio`**, not the other way. That took
breaking two vocabulary ties, and both are worth understanding before adding anything to these
contracts:

- **`Audio3DParams` speaks `System.Numerics.Vector3`**, not the XNA one. There are exactly two
  construction sites (`SoundBank.Build3DParams`, `SoundEffectInstance`), both converting through
  `AudioVectorInterop.ToNumerics()`. 3D audio is per-emitter, not per-vertex, so the copy is
  nowhere near a hot path.
- **`IAudioBackend.GetMicrophones()` returns `MicrophoneInfo[]`**, a plain `(handle, name)`
  descriptor, and the framework builds its own `Microphone` objects from it. The seam was already
  inconsistent here — every other microphone member took a raw `uint` handle.

**The rule this establishes: a contract in the engine tier must not name a game-facing XNA
identity.** Those are the types the patcher rescopes so games bind them, they live in
`WPR.Framework.Xna`, and the framework consumes the seams — so naming one makes the reference
un-invertible. That is why the four seams which *do* name them (`IGraphicsBackend` → `Texture2D`,
`IInputBackend` → `GamePadState`, `IPlatformBackend` → `GameWindow`, and `IKeyboardEmulationHost` →
`Keys`/`DisplayOrientation`) are still in `WPR.Framework.Xna` under `WPR.Xna.Rhi`. Audio escaped
because its two ties were three floats and a two-field struct.


**`WPR.Abstractions` is gone (2026-09-01), and the rule that replaced it matters more than the
deletion.** It was meant to be the linchpin every layer implemented. It ended up with 14 types, 11
of which nothing referenced; the three that were real each belonged beside the registry that hands
them out:

| Contract | Now in | Beside |
|---|---|---|
| `IAudioTranscoder` | `WPR.Engine.Audio` | `AudioTranscoderBackend` |
| `IAccelerometerProvider` | `WPR.Engine.Sensors` | `SensorBackend` |
| `IGameHost` | `WPR.Engine.GameLoop` | — its own project, so `WPR.Backend.FNA` can implement it without referencing the composition root |

**A contract belongs with the subsystem that composes it, not in a shared project named after the
fact that it is abstract.** A bucket of interfaces attracts speculative ones — that is how eleven
unconsumed types accumulated, including three that *looked* used and were not (`IInputProvider`'s
only mention was a comment; `IStorageProvider`'s only "consumer" was Avalonia's unrelated type;
every `ScreenOrientation` hit was Android's own enum). When you add a contract, put it in the
`WPR.Engine.*` project that owns its registry.
**`XnaBackend` no longer has audio slots.** The framework's `SoundEffect` / `Cue` / `MediaPlayer`
read `AudioBackendRegistry.Sound` / `.Xact` / `.Media`. `XnaBackend` keeps graphics, input,
storage, platform, achievements, tilt and the per-launch hooks.


**Notifications are `WPR.Engine.Notifications`** (2026-09-01). The `DesktopNotifications` API
(`INotificationManager`, `Notification`, the event args) plus `NotificationBackend`, the registry a
head fills through `caps.Notifications(...)`. It was in `WPR.Common` — the assembly things land in
when they have no home — and a notification API is not a general utility.

`NativeUI.NotificationManager` is gone; use `NotificationBackend.Manager` / `SetManager`, named
like every other subsystem registry. **Null is the normal unset state**, not an error: a platform
that declares no manager shows no toasts while still awarding and persisting the achievement, which
is precisely what Android did silently for a long time when nothing assigned the old holder.

The namespace stays `DesktopNotifications` — it is a vendored third-party API shape and both heads'
implementations sit under `DesktopNotifications.Windows` / `.Android`.
### Audio implementations plug in as modules (2026-09-01)

> **Superseded in part by the section above.** The three seams described below moved to
> `WPR.Engine.Audio` later the same day, along with the registry; where this text says they live in
> `Src/Core/WPR.Framework.Xna/Backend/`, read `Src/Engine/WPR.Engine.Audio/`. The module contract,
> the stack semantics and the lifetime rules are unchanged and still accurate.

Runtime audio is **three seams**, all declared in `Src/Core/WPR.Framework.Xna/Backend/`:

| seam | what it backs | why it is its own seam |
| --- | --- | --- |
| `IAudioBackend` | `SoundEffect` / `SoundEffectInstance` / `DynamicSoundEffectInstance`, `Microphone` | the sound-effect mixer and 3D positioning |
| `IXactBackend` | `AudioEngine` / `SoundBank` / `WaveBank` / `Cue` | genuinely optional (most titles ship no banks) and owns a native callback's delegate lifetime |
| `IMediaBackend` | `MediaPlayer` / `Song` **and** `VideoPlayer` / `Video` | one XNA subsystem with one lifetime, even though FNA fills it from two libraries |

All three sit **higher than the C ABI** on purpose — see the rationale on `IAudioBackend` itself.
That is what lets a non-FAudio implementation exist at all.

**Implementations are peer projects under `Src/Modules/Audio/`, not part of a platform backend or head.**

| project | TFMs | fills |
| --- | --- | --- |
| `WPR.Audio.FAudio` | `net8.0;net8.0-android` | all three seams — FAudio, FACT, and FAudio's `XNA_Song` + Theorafile |
| `WPR.Audio.AndroidMediaPlayer` | `net8.0-android` | **the song half of `IMediaBackend` only** |

Both were somewhere worse before the split: the FAudio adapters were three files inside
`WPR.Backend.FNA` (the *graphics and game-loop* host, which carried them only because FNA.dll
happens to compile the FAudio bindings in too), and the Android one was head code that reached into
`WPR.Backend.FNA` for its video half.

#### The plug: `IAudioModule` + `AudioBackendRegistry`

A module is a named unit that fills any subset of the three seams. Each factory receives **the
module below it in the stack**:

```csharp
public sealed class AndroidMediaPlayerModule : AudioModule
{
    public override string Name => "AndroidMediaPlayer";
    public override IMediaBackend CreateMedia(IMediaBackend? next) =>
        new AndroidMediaPlayerBackend(next);   // `next` is the video half
}
```

`AudioModule` (the base) returns `next` for every seam, so a module overrides only what it
implements. Three cases, and the third is the one that shaped the signature:

- **fills the seam** — ignore `next` (`FAudioModule`).
- **doesn't fill it** — return `next` unchanged (inherited).
- **fills *part* of it** — keep `next` and delegate the rest to it. This is Android: songs are its
  own, video is forwarded. Passing the delegate in rather than letting the module `new` one is
  exactly what keeps `WPR.Audio.AndroidMediaPlayer` free of any reference to `WPR.Audio.FAudio`.

**Why the contract and registry sit in `WPR.Framework.Xna` and not in a `WPR.Audio` project of
their own** (asked and settled 2026-09-01). The three seams *cannot* leave: `IAudioBackend` speaks
`Vector3` and returns `Microphone[]`, both defined there, while `SoundEffectInstance` / `Cue` /
`MediaPlayer` in that same assembly consume the seams — a contracts project holding them would need
`WPR.Framework.Xna` and be needed back by it. Only `IAudioModule` + `AudioBackendRegistry` could
move, which would put the seam and its registry in different assemblies and make every
implementation reference two projects instead of one. It is also the same call as
`IAchievementStore`: the vocabulary here is entirely XNA audio types, so it belongs beside them,
whereas `IAccelerometerProvider` earned a neutral home because a motion sample is three floats. And
`AudioBackendRegistry` is the direct sibling of `XnaBackend`, which fills the graphics/input/storage
slots from the same folder. It adds no dependency to the assembly games bind — only `System`,
`System.Collections.Generic` and `XnaBackend`.

Two registration kinds, and the distinction is load-bearing:

- `AudioBackendRegistry.SetBase(...)` — the implementation of last resort. **`FnaGameHost` sets
  `FAudioModule`**, not a head, so *any* code path that runs a game has audio — including the
  bare-`FnaGameHost` console harness (see "Launching one game without the launcher UI"), which
  never reaches a head's `ServicesSetup`.
- `AudioBackendRegistry.Register(...)` — what a head calls in `ServicesSetup.Start()` to layer over
  it. Re-registering the same `Name` **replaces in place** rather than appending, because Android
  recreates its process straight into any activity and `GameActivity`'s `:game` process runs the
  composition root again.

The base is composed first regardless of call order — which matters, because the head registers at
launcher startup and the host sets the base per launch.

**Lifetimes are split, and getting this wrong is the trap the old `MediaBackendOverride` existed to
work around.** Modules are process-lifetime and are deliberately not cleared by `XnaBackend.Clear()`
(same reasoning as `SetAchievements`: clearing would leave the *second* game launched without its
platform audio). The backends they build are per-launch — `Compose()` runs once per game and
produces fresh instances — so **a module must hold no per-game state of its own**.

`Compose()` never lets audio take a launch down: a module whose `IsAvailable` or factory throws is
skipped, the stack below it stands, and the failure goes to `XnaBackend.LogWarn`. A seam nobody
filled is left *unset* rather than being handed a null, so the accessor's own "No IAudioBackend has
been registered" message is what a caller sees.

**Read the composition out of the launch log.** `FnaGameHost` writes one line per game:

```
[wpr-audio] sound=FAudio xact=FAudio media=AndroidMediaPlayer
```

That is the first thing to check for any "no sound on one platform" report — it says which
implementation actually served the run, before you go looking at the game.

#### Adding a third implementation

Add a project under `Src/Modules/Audio/`, reference `WPR.Framework.Xna` (and nothing else, unless the
native bindings force it), implement the seams you cover, derive an `AudioModule`, and
`Register` it from the head that wants it. Then:

- add it to `WPR.sln`'s **Audio** solution folder and to the relevant `.slnf` (android-only
  projects go in `WPR.Android.slnf` only — `Directory.Build.targets` never strips a singular
  `<TargetFramework>`, so the filter is the exclusion mechanism);
- if it must reference FNA, add it to `BackendIsolationTests.AllowedReferrers` **and say why**.
  `WPR.Audio.FAudio` is there because the FAudio/FACT P/Invokes are compiled *into FNA.dll* and
  FNA's `DllImport` resolver (`FNADllMap`) fires only for P/Invokes whose declaring assembly is
  FNA — re-declaring the natives elsewhere breaks native library resolution.
  `WPR.Audio.AndroidMediaPlayer` is deliberately **not** there, and that is the shape to aim for.

No `ApplicationPatcher.Version` bump and no reinstall for any of this — no patcher table changed
and no IL is rewritten. Games pick it up on next launch.

**Two things that did *not* move.** The install-time transcoders
(`Src/Platforms/*/Audio/`, next section) are a different seam entirely —
`WPR.Abstractions.Audio.IAudioTranscoder`, a file-in/file-out install-time concern — and the
Android one is a bound `Service` in the head's manifest, so both stay in their heads. And
`FAudioSoundBackend`/`FAudioXactBackend` need `InternalsVisibleTo` from **both** `FNA` (for the
global-namespace bindings and `FNAPlatform`'s microphone capture) and `WPR.Framework.Xna` (for
`Microphone`'s ctor and `MonoGame.Utilities.FileHelpers`); if you split these files further, the
grants have to follow.

### Install-time audio transcoding is behind a seam too

Same three-part shape as sensors and achievements (2026-08-31):

* **Contract** — `WPR.Abstractions.Audio.IAudioTranscoder` (+ the `AudioTranscodeResult` DTO). Its
  whole vocabulary is file paths, so unlike `IAchievementStore` it has no reason to live outside
  Abstractions.
* **Registry** — `WPR.Core.AudioTranscoderBackend`, in `WPR.Loader` beside its consumer
  `AudioCompabilityConverter`, exactly like `SensorBackend` sits beside `Accelerometer`.
* **Implementations** — `WPR.Platform.Windows/Audio/FFMpegCoreAudioTranscoder.cs` (FFMpegCore over
  the bundled `ffmpeg.exe`) and `WPR.Platform.Android/Audio/FFmpegKitAudioTranscoder.cs`
  (ffmpeg-kit over JNI). Registered in each head's `ServicesSetup.Start()`.

**Why it exists.** WP7 XNA titles ship soundtracks as `.wma` (Mirror's Edge has 40+ tracks under
`Content/music/`, each with a 129-byte `.xnb` Song stub), but the song backend decodes Ogg Vorbis
only — FAudio's `XNA_PlaySong` is stb_vorbis. `ApplicationInstaller` therefore transcodes at install
time. That code used to call FFMpegCore directly from `WPR.Loader` under a comment claiming it was
the implementation "for all platforms". It was not: **FFMpegCore spawns an `ffmpeg` child process**,
and an APK has no executable to spawn. On Android every transcode failed, the exception was
swallowed per file, the install still reported success, and the game was silently mute — sound
effects worked, because those are XNB `SoundEffect` and need no conversion. This is the same mistake
the sensors split fixed: a desktop-only implementation on a shared Core project, also shipped inside
the APK for nothing.

> **Superseded 2026-10-14** (see "Every game is attempted; what it carries on past reaches WPR
> Hub"): a failed transcode no longer fails the install. The game installs, the failure goes to WPR
> Hub as an `install-problem` report, and the reasoning below (a mute game nobody can see) is why
> the report exists. The old early return also left a half-installed row behind the error dialog.

**A missing transcoder now fails the install** (`ApplicationInstallError.ConvertFailed`) rather than
degrading. That is the deliberate difference from `SensorBackend`, where "no provider" means "no
readings": a missing transcoder produces a game that installs cleanly and plays no music, which the
user cannot see or diagnose. Individual files are still per-file warnings — one bad track shouldn't
block an install — but *every* track failing throws, because that is the transcoder not working.
Consequence: **any code path that installs must compose a transcoder.** `BatchReinstall`
(`--reinstall-all` / `--repatch-installed`) does its own registration because `Program` runs it and
`Environment.Exit(0)`s without ever reaching `ServicesSetup.Start()`, which lives in
`MainWindowDesktop`'s ctor.

`RepatchAsync` re-runs the transcode as a **non-fatal** step (it did not touch audio at all before
this). That is the cheap path for a game already installed on Android and therefore mute — a repatch
fixes its soundtrack without a full uninstall/reinstall, and the Android head already exposes one in
`GamesActivity`. It's idempotent and nearly free when there's nothing to do: the converter sniffs
each file's container (`OggS` vs the ASF magic) and skips what's already converted.

**The transcoded file keeps the `.wma` filename** — the `.xnb` Song stub names that path and is not
rewritten, so the extension deliberately lies about the container afterwards. That is exactly why
`MediaPlayer.IsSupportedSongPath` sniffs for `OggS` instead of trusting the extension; don't
"simplify" it back to an extension check.

**The `com.arthenica.ffmpegkit` binding was a shell until this change.** It, and both
`com.arthenica.smartexception*` projects, targeted plain `net8.0` — and a Java binding is only
generated on an *android* TFM, so `class-parse` never ran and all three built ~4 KB assemblies with
no types in them. Nothing in the repo could call FFmpegKit because there was no FFmpegKit to call.
All three now target `net8.0-android` with `IsBindingProject` / `AndroidClassParser=class-parse`,
modelled on `Org.Libsdl.App`, which was the only binding project here that was ever correct. Check
the fix survives by looking for the Java class in the APK, not just for a green build:

```powershell
# expect a non-zero count; it was 0 before 2026-08-31
unzip -p <apk> classes.dex | Select-String -Encoding Byte "com/arthenica/ffmpegkit/FFmpegKit"
```

The native side needed nothing: `libffmpegkit.so` / `libavcodec.so` / `libavformat.so` /
`libswresample.so` were already checked in under `Libraries/<abi>/` and already in the APK, the
bundled ffmpeg is configured `--enable-libvorbis` (the encoder), and `libavcodec` carries the
`wmav1` / `wmav2` / `wmapro` decoders. The bundled build is
`ffmpeg-kit-audio-<abi>-4.5.1-lts` (ffmpeg v4.5-dev).

**Songs do not go through FAudio on Android.** `AudioBackendRegistry` composes the `IMediaBackend`
slot per game launch from the registered audio modules; the Android head adds
`WPR.Audio.AndroidMediaPlayer.AndroidMediaPlayerModule` (platform `Android.Media.MediaPlayer`) in
`ServicesSetup.Start()`, and it wins the song half because it is registered above the FAudio base.
A *module* rather than a direct `XnaBackend.SetMedia` call because that slot is per-launch and
cleared on teardown — a head that registered the backend itself at startup would be overwritten by
the next launch. (Before 2026-09-01 the same job was done by a bespoke
`WPR.Backend.FNA.MediaBackendOverride`, which could plug the media seam only; see the audio
architecture section above.)

The reason is a defect in FAudio's own song player, not in WPR. `XNA_SongSubmitBuffer`
(`XNA_Song.c`) decodes exactly `sample_rate * channels` frames — **one full second** — into a
single reusable cache, with a **queue depth of one**, refilled from `OnBufferEnd`. `OnBufferEnd`
fires when the buffer has already finished, so at every boundary the voice has nothing queued
*and* the audio thread is decoding a second of Vorbis inside the mixer callback. Desktop absorbs
it; on a phone it is an audible click **exactly once per second**, which is the tell.

Rebuilding FAudio with a double-buffered `XNA_SongSubmitBuffer` (two caches, prime twice,
alternate) is the better fix and would help desktop too — but `libFAudio.so` / `FAudio.dll` ship
**prebuilt and checked in**, the vendored `lib/FAudio/src/*.c` is not compiled by any build here,
and this machine has neither an NDK nor cmake. Hence the platform-player swap.

Two things about `AndroidMediaPlayerBackend` worth knowing: it delegates the **entire video half**
to the module below it in the stack (`FAudioMediaBackend`'s Theorafile — handed in as a ctor
argument, so this project names no other audio implementation), and it relies on MediaPlayer
sniffing **content, not extension** — our transcoded songs keep the `.wma` filename. That
assumption is verified: logcat shows `allocate(c2.android.vorbis.decoder)` and
`read media type: audio/vorbis` on a `.wma`-named file. If songs ever silently fail to start,
re-check that first.

**Owning the song player means owning its lifecycle — four things FAudio used to do for free.**
Each of these was a real defect, not a hypothetical:

- **Nothing else will stop the music.** Sound effects go quiet when the app backgrounds because SDL
  pauses FAudio's audio device as part of the Android activity lifecycle; a platform `MediaPlayer`
  is ours alone and played on over the home screen. `GameActivity.OnPause`/`OnResume` now call
  `AndroidMediaPlayerBackend.SuspendForBackground()`/`RestoreFromForeground()`. It claims only a song that
  was *actually playing*, so a game that paused or stopped its own music on deactivation keeps that
  state instead of having music restarted under its pause menu.
- **A paused player may not survive the background.** Android can reclaim the audio track while the
  app is away, so `ResumeSong` cannot just call `Start()` — it captures the offset on every pause
  and rebuilds the player with `SeekTo` if the resume throws. Swallowing that exception (the
  original code) silently killed music for the rest of the session, because nothing upstream ever
  re-issues `PlaySong` for a song it believes is merely paused.
- **An errored player never raises Completion**, so `_ended` must be latched from the `Error`
  callback too, or the XNA queue polls `GetSongEnded()` forever and every later track is lost.
- **Pausing once is not enough — being backgrounded has to be a latched state.** `OnPause` runs
  `base.OnPause()` first, and SDL only blocks the game thread when that thread next pumps events, so
  the game keeps running either side of our suspend (and again on resume, between the thread
  unblocking and `RestoreFromForeground`). A WP7 title reacting to `Game.Deactivated` / `Activated`
  routinely *stops and replays* its track in exactly that window — and a `PlaySong` arriving after
  our suspend used to start a brand-new player at full volume with nothing left to stop it:
  `SuspendForBackground` had already run and would not run again until the app had been foregrounded
  and backgrounded once more. That is the "music keeps playing in the background, but only the first
  time" report (Angry Birds, 2026-08-31). `AndroidMediaPlayerBackend._hostBackgrounded` is the fix: set
  unconditionally at the top of `SuspendForBackground`, cleared first thing in
  `RestoreFromForeground`, and honoured by `StartPlayerLocked` (Prepare but do **not** `Start()` —
  starting-then-pausing emits a burst of audio first) and by `ResumeSong`. A song held that way
  marks itself `_suspendedByHost`, so the restore is what starts it. Keep the two flags distinct:
  `_suspendedByHost` is per-song and `StopSong` clears it; `_hostBackgrounded` is a property of the
  *activity* and must survive the game stopping and starting tracks while away.

**Backgrounding restarts the music, and that is correct** — do not "fix" it. A WP7 title stops its
own song from its `Deactivated` handler (real hardware tombstoned the app) and calls `Play()` again
on reactivation; XNA 4.0 on Windows Phone has no `Play(Song, TimeSpan)`, so `Play` means "from the
beginning". Measured for Mirror's Edge: our background pause at 37665 ms, then
`StopSong (suspended=True, ended=False)` from the **game thread** 103 ms later. The `ended=False` is
the load-bearing part — no `Completion` fired, so this is not the XNA queue advancing, it is the
game. Carrying the offset across that stop/replay was built and then deliberately reverted
(2026-08-31): it sounds nicer but silently overrides a game that restarts a track on purpose.
Position is preserved only where XNA actually defines it — `Pause` then `Resume`.

The `[wpr-media] StopSong (suspended=…, ended=…)` line exists precisely to tell those two apart;
they are otherwise indistinguishable from inside the backend.

**Do not use the emulator to judge any of this.** API 36 mutes and tears down background playback
by itself — logcat says `AS.AudioService: AudioHardening background playback would be muted` — so
the song track dies on backgrounding there whether or not we handle it. That masked the first bug
above completely; it was reported from a real device.

**Which thread ffmpeg-kit runs on is the whole story.** Never the UI thread and never a .NET
thread-pool thread — both failure modes were observed on the emulator and neither is obvious from
a build:

| how the sync API was called | what happened |
| --- | --- |
| on the UI thread (the natural result of `await`ing an install started from an activity) | works, but the main thread sits in `sem_wait` inside `libmonosgen` for the whole soundtrack — a multi-minute ANR, "WPR isn't responding" |
| on a .NET thread-pool thread (`Task.Run`) | **never returns.** ffmpeg emits no session log at all, the conversion stalls on the first file, and the app stays responsive — so it looks like a hang with no error anywhere |

**Superseded on 2026-09-01: it now runs the SYNC entry point on a dedicated `Thread` we create**
(`FFmpegKitAudioTranscoder.RunOnOwnThreadAsync`). The table above still holds — a thread of our own
is neither the UI thread nor a pool thread — and it avoids ffmpeg-kit's executor, whose
`pool-N-thread-*` threads live for the whole process and, given a completion callback, run managed
code and stay attached to the runtime. 36 tracks still convert in well under a minute on the
emulator.

### The transcode runs in its own process, and that is not optional

**Running ffmpeg-kit destroys the process it runs in.** Once a transcode has happened, the next
Mono stop-the-world never completes: main, `.NET Timer`, `.NET TP Gate` and a worker end up parked
in `sigsuspend` — they took the suspend signal and never received the restart — with no CPU, no log
and no exception. The transcode itself always succeeds, every track lands on disk as Ogg Vorbis, so
nothing looks wrong until the *next* thing that allocates hard. That is why it was reported as
"installing a second game in one launch hangs" (2026-09-01) rather than as an audio bug.

Measured on Pixel_Dev, cold-booted, ~1 GB free:

| sequence | result |
| --- | --- |
| two installs, neither with `.wma` | fine — main thread idle in `do_epoll_wait` |
| one install with `.wma`, then any second install | second hangs at "reading manifest" for ever |
| same, but ffmpeg on a `Thread` we own instead of ffmpeg-kit's executor | identical hang |

So it is **not** the executor threads, not memory, not package size, and not UI-thread work (that
was a separate ANR — see `ReadPreview` below). The suspect is ffmpeg's native code replacing the
signal handlers Mono's suspend/restart protocol relies on; nothing reachable from managed code
survives it.

**The fix is process isolation** — the same answer `GameActivity` gives for a game run, and for the
same reason: a process that has done the unrecoverable thing is disposable, so do the
unrecoverable thing somewhere disposable. `TranscodeService` (`Process = ":transcode"`) owns
ffmpeg-kit; `RemoteAudioTranscoder` is what the launcher registers and forwards each file over a
`Messenger`. Four things about it are load-bearing:

- **The service kills its own process** after `IdleShutdownMs` of quiet. The `IAudioTranscoder`
  seam is per-file and has no "batch finished" signal, and inventing one would push Android's
  problem into a contract the Windows head shares. An idle timer needs no protocol and guarantees
  the next batch gets a fresh process even if this one already wedged.
- **The client must `UnbindService` when the far end dies.** While a binding is outstanding Android
  treats the process ending as a crash and restarts it (`Scheduling restart of crashed service …
  for connection`) — with a self-terminating service that is an endless spawn/idle/kill loop.
- **Idle shutdown is a delayed `Message`, not a delayed `Runnable`.** Cancelling a posted Runnable
  matches on object identity and every C# delegate handed to the binding is wrapped in a fresh Java
  object, so `RemoveCallbacks` would silently never match. `RemoveMessages(what)` has no such trap.
- **`RemoteAudioTranscoder.IsAvailable` is not cached**, unlike the in-process one: the answer
  genuinely changes between batches because the process behind it is torn down between them.

Verified 2026-09-01: three installs in one launch — Bejeweled LIVE (5 tracks), Mirror's Edge
(136 MB, 36 tracks) and Angry Birds — all succeeded, 36/36 and 5/5 tracks converted to `OggS`,
launcher main thread still in `do_epoll_wait` at the end, zero `sigsuspend` threads, zero ANRs, and
exactly one `:transcode` process spawned and reaped per batch.

`FFmpegKitAudioTranscoder` still exists and is still the thing that runs ffmpeg — it just runs
inside `:transcode` now. It also runs the **synchronous** entry point on a `Thread` it creates
(`RunOnOwnThreadAsync`) rather than ffmpeg-kit's executor: that did not fix the wedge, but it means
nothing foreign stays attached to the runtime and the thread exits with the track.

Independently, `ScanWmaAndConvert` uses `ConfigureAwait(false)` and both call sites wrap it in
`Task.Run` — the install is kicked off from the UI thread in both heads, so without that its
continuations (container sniffs, `File.Move`s) all post back there.

**No `ApplicationPatcher.Version` bump for any of this** — no patcher table changed and no IL is
rewritten. Audio conversion is a file operation, so a *reinstall* (or the repatch above) is what
picks it up, not a patcher-version-driven staleness check.

Verified end to end on the `Pixel_Dev` emulator (API 36 x86_64) on 2026-08-31: Mirror's Edge
installed through the document picker, `[AppAudioConverter] FFmpegKit available (ffmpeg
v4.5-dev-…)` → `Transcoding 36 .wma file(s)`, all 36 rewritten to `OggS` with `.wma.original`
siblings kept and no `.new.ogg` left behind, no ANR, UI taps ~60 ms throughout. Byte sizes match
the desktop FFMpegCore output (e.g. `Ambience_01` 860,416 vs 860,415).


**Namespaces did not change with the move** — the catalogue types are still `WPR.Models`,
exactly as the Stage 2 split intended ("split by assembly, not namespace"). The ~30 files
across both heads that consume them needed no edit; they just get the assembly by reference.

How `Data/` reaches each head:
- **Windows**: the `Copy pre-made database` target copies `Data\**` into
  `$(OutputPath)\Database\`; `Program.cs` copies from there into `%LocalAppData%\WPR\Database`
  on first run if absent.
- **Android**: the csproj links `Data\**` in as `AndroidAsset` with
  `Link="Database\%(RecursiveDir)%(Filename)%(Extension)"`, so they land at
  `assets/Database/...` in the APK, which is what `WprStartup.CopyFileFromAssets` reads.

**Do not add a step that copies the data into a platform head's project directory.** The
android head used to do exactly that so it could glob the copies as assets, and 1115 of them
were committed — the whole data set duplicated in git, plus 2 stale files in the windows head.
`.gitignore` now blocks both paths.

One thing deliberately did NOT move: `Achievement` (the entity) stays in
`WPR.Framework.Xna/GamerServices/`, because it is a **game-facing** type the patcher rescopes
there. Only the context moved. That asymmetry — entity fixed in place, context free to leave —
is precisely why the seam above exists rather than a plain project move.

- **Install pipeline** (per game, runs once when the user clicks Install on a
  newly-discovered `.xap`/XNA folder):
  1. `LibraryScanner` discovers the package.
  2. `ApplicationInstaller` unpacks to `%LocalAppData%\WPR\AppData\<ProductId>`
     (the folder name is `Application.DataStoreFolder`).
  3. `ApplicationPatcher.PatchDll` rewrites every `*.dll` in the install dir:
     Silverlight / WP / XNA types redirected to our shims (`Patches` dict),
     XNA types rescoped to `WPR.Framework.Xna` (`WprFrameworkXnaTypes` set),
     a handful of CLR methods redirected (`MemberPatches` dict).
  4. `XnaAchievementSeeder.SeedAsync` populates the SQLite achievements DB.
- **Game launch loads the patched DLLs.** If the patcher table changes, every
  game installed before the change still has the old IL — it must be
  **reinstalled** to pick up new redirects. The user knows this; I should say
  "reinstall <game>" rather than "rebuild" when the fix lands in
  `ApplicationPatcher.cs`.

### Silverlight `MessageBox.Show` blocks for a real answer (2026-10-16)

`System.Windows.MessageBox.Show(text, caption, button)` used to **NullReference on every call**:
the shim's `ShowSimpleImpl` was never assigned, because both heads assigned a same-named field on
their own `internal System.Windows.MessageBox` placeholder (string-typed, returning null) with the
button/result mapping commented out. Measured over the desktop installs: Big Buck Hunter Pro
(OKCancel to quit a hunt, log out, buy), Galactic Reign, ISE titles, GoogleAnalytics and
WinRTBridge (Unity) call it.

The shape now: **the shim owns the mapping, a head only shows buttons.**
`WPR.WindowsCompability.MessageBox.Presenter` is `(caption, text, labels) -> Task<int>`, the index
pressed or **-1 when dismissed**. `Describe`/`Resolve` are the documented WP7 rules: `OK` is OK "in
all cases" (dismissing included); `OKCancel` is OK only for the OK button, Cancel for Cancel,
**Back**, Esc or closing the window. YesNo/YesNoCancel (desktop-only values the enum also has)
map as WPF (dismiss = No / Cancel). Labels are WP7's lower-case "ok"/"cancel". `Show` blocks the
game thread on the task (the game stops drawing, as under WP7's box); no presenter, or one that
throws, answers as dismissed. Unit tests: `MessageBoxTests`.

| head | presenter | the exits that complete the task |
| --- | --- | --- |
| Windows | `MessageBoxUtils.ShowGameMessageBoxAsync`, an Avalonia window built directly, topmost (the game window is SDL's and cannot own it), dispatched to the UI thread; gives the foreground back to the game window on close | a button, Esc, the close button, `Closed` |
| Android | the same name over an `AlertDialog` on `MessageBoxUtils.MainActivity` (GameActivity in `:game`) | a button, cancel (Back, outside tap), dismiss |

**Every exit must complete the task**, or the game thread waits for ever. That is why Windows does
not use MessageBox.Avalonia: 2.2.0 predates the head's Avalonia, `GetMessageDialogResult` falls
into its MissingMethodException fallback window, and that window never completes when closed.

**XNA's `Guide.BeginShowMessageBox` uses the same presenter, and the package is gone (2026-10-23).**
WPR Hub group `8bf2d758d13f` (`MissingMethodException: AvaloniaObjectExtensions.Bind(IAvaloniaObject, …)`
from `MsBoxStandardWindow.!XamlIlPopulate`, 0.2.06/0.2.07, PvZ, Pool Pro Online 3, Doodle Jump,
Fruit Ninja, Sonic 4, Bejeweled LIVE) was every Guide box on the desktop: the fallback window
showed hardcoded OK/Yes/No instead of the game's labels, answered Yes as index 1 (the game's
*second* button), and hung on close. Now:

- `Guide.ShowMessageBoxFunc` answers the index pressed or `Guide.MessageBoxDismissed` (-1), and
  `EndShowMessageBox` returns **null** for -1 ("null if the user cancelled", XNA's documented
  answer for Back). It used to return the focus button, i.e. a dismissed "Quit?" could mean Yes.
  `Guide.ResolveMessageBoxResult` is the mapping; a missing or throwing presenter answers null.
  `BeginShowMessageBox` brackets the presenter with `SuppressFocusActivation(2 s)`, like Show.
  Log: `[wpr-guide] "<title>" -> "<label>" (i)` / `dismissed (null)`.
- Both heads pass the game's own labels and focus button to `ShowGameMessageBoxAsync`
  (Windows: a 4-argument overload making the focus button the default).
- The Windows head no longer references **MessageBox.Avalonia**. `MessageBoxUtils` builds every
  dialog in code on one window (`ShowDialogWindowAsync`): launcher dialogs
  (`GetMessageDialogResult`, now typed `DialogButtons`/`DialogAnswer`/`DialogIcon`, owned by
  `MainWindow` when it is visible), the keyboard input box (`GetInputResult`, null on cancel, Guide
  answers the default text) and game boxes. Do not bring the package back.
- Android does not reference it either: its `GetMessageDialogResult` (typed on the package's enums,
  with no callers left) is deleted, `ShowSelectableErrorAsync` is a one-button
  `ShowGameMessageBoxAsync`, and `GetInputResult` completes on Back / dismiss too. Neither head
  references MessageBox.Avalonia any more.

Verified: a probe over the real Avalonia 11.3.9 window (each button, Esc, close, owned and unowned,
from the UI thread and a worker; 16/16), and PvZ's first-run "Screen lock settings" Yes/No box in
`scratchpad/gamerun` with the real window on its own Avalonia UI thread (`GuideHook.cs`, local:
`GAMERUN_MSGBOX=<index>|esc|close,...`). The box only appears until PvZ has saved, so clear
`IsolatedStore/706f822a-…` in the scratch root between runs. Doodle Jump's quit prompt on Back is
drawn by the game itself, not a Guide box.

**Focus.** The dialog takes focus, and the SDL focus events wait in the queue until the game thread
pumps again, so `Show` calls `XnaBackend.SuppressFocusActivation(2 s)` before and after: WP7 raised
no Deactivated/Activated for its own box, and a spurious resume re-raises the game's `Activated`
(see "Only the FIRST Game.Activated is the cold start"). Log: `[wpr-msgbox] "<caption>" (OKCancel)
-> cancel = Cancel` through `Trace`, so it is in the per-game log.

Verified in the desktop harness (Big Buck Hunter Pro, hunt -> Back -> PAUSED -> MAIN MENU, which asks
"This will delete your progress ... ?" OKCancel): ok returns to the main menu, cancel and dismissed stay
paused. The harness answers through `GAMERUN_MSGBOX=<index>[,...]` (a local addition to
`scratchpad/gamerun`); the Avalonia window and the AlertDialog were built, not clicked. Reaching that
screen needed `DOTNET_JITMinOpts=1`: the game's loader spin-waits on plain fields
(`while (mStateManager == null);`), which the optimising JIT hoists, so every hunt otherwise hangs on
LOADING. That is a separate bug, not fixed here.

Shim and head behaviour only: no `ApplicationPatcher.Version` bump, no reinstall.

### The Silverlight host's thread has a SynchronizationContext (2026-10-22, patcher v54)

`Dispatcher.AttachToCurrentThread` (called by `SilverlightGame`) now also installs a
`DispatcherSynchronizationContext` (Silverlight's type, posting through the dispatcher queue drained
once a frame); `Detach` restores the thread's old one. Before, `SynchronizationContext.Current` was
null on that thread, so everything that returns "to the thread that started it" ran on the thread
pool: `BackgroundWorker.RunWorkerCompleted` / `ProgressChanged`, and every `await` continuation in a
WP8 app. **Minesweeper (WP7, `0b00c4a3-…`)** fills its board in a BackgroundWorker and builds the
page in RunWorkerCompleted; on a pool thread that raced the page's layout and killed the process
(`NullReferenceException` in `GameplayPage.ap`, unhandled) in most runs that started a game.
Measured: 3 of 3 runs crashed before, 0 of 3 after, on both the old and the new build.

**The trap: WPR code that blocks the host thread on a task.** An `await` inside it captures this
context, its continuation is queued for the thread that is blocked, and the game hangs. Checked when
this went in: the GamerServices sync calls (`GetProfile`, `GetAchievements`, `EndShowMessageBox`…)
wait on `Task.Run(async …)` work, which runs with no context; `LeaderboardReader` uses
`ConfigureAwait(false)`; the message box presenters dispatch to their own UI thread. **New
sync-over-async on the host thread needs `ConfigureAwait(false)` or `Task.Run`.**

The patcher entry (v54) is for apps naming the type; not identity-binding. Regression (desktop
D3D11, against 2947a8de): the 7-title set and Minesweeper WP7, Pirates!, Big Buck Hunter Pro, Ice
Age Village unchanged apart from animation phase; Fox News and YouTube unchanged on D1-D4.

### PointAbout Mashup apps: what they need from the platform (2026-10-21, patcher v52)

Hub group "`ResourceDictionary.get_MergedDictionaries()` not found" (0.2.06 Android): Fox News
Latest Headlines (`3c10c9db-…`, PointAbout's "Mashup" framework) and YouTube (`dcbb1ac6-…`, WP8).
Fox News was walked past each missing member in turn. Every fix is platform-side; the app's own
DLLs (Mashup, LazyListBox, the toolkit, ad SDKs) run as shipped.

| member | was | now |
| --- | --- | --- |
| `ResourceDictionary.MergedDictionaries` | `List<>` | `PresentationFrameworkCollection<ResourceDictionary>`, Silverlight's type (IL binds the getter by return type) |
| `ResourceDictionary.Remove(string)` / `Remove(object)` | `bool` | `void`, as Silverlight |
| `<sys:String>`, `sys:Double`, `sys:Int32`, `sys:Boolean` resources | `Activator` failed on `string`, element skipped | value from the element text |
| `ItemsControl.ItemsPanel` | stored only | a StackPanel / VirtualizingStackPanel template sets the control's Orientation (a Grid already had its layout) |
| `ItemsControl.ItemContainerStyle` | missing | applied to each container when the style's TargetType fits |
| `VirtualizingStackPanel`, `VirtualizationMode` | missing (v52: shim types bind on repatch) | a StackPanel that realises every item |
| `PhoneApplicationFrame.Orientation` | missing | the displayed page's orientation |

`ApplicationBar`, `ListBoxItem`, `ItemsPanelTemplate` and content alignment came from this branch
already.

**ListBox has Silverlight's default visual tree and draws through it** (step 2, same day):
ScrollViewer named `ScrollViewer` > `ItemsPresenter` > the items panel (`ItemsPanel`, a
`VirtualizingStackPanel` by default) > the containers. Fox News bundles **LazyListBox**, which takes
that ScrollViewer from `GetTemplateChild` in `OnApplyTemplate`, finds the VirtualizingStackPanel
below it through `VisualTreeHelper`, and reads containers through
`VirtualizingPanel.ItemContainerGenerator`. Without the tree its `ArrangeOverride` threw "can only
be used with a virtualized list" and the whole page painted black. What that takes:

- `ItemsControl.ItemsHost` / `HostChildren`: where the containers live. It is the control itself
  (an ItemsControl here is still a StackPanel) unless a subclass hosts a real panel; `ListBox` does.
  **Everything meaning "the containers" goes through `HostChildren`**: ItemsControl, Selector,
  ItemContainerGenerator and the touch router's row selection. `Children` is only the control's own
  visual. Game IL never sees the difference: Silverlight's ListBox is not a Panel.
- `ItemsPresenter` is a `Panel` holding the items panel (Silverlight's is a FrameworkElement), so
  layout, the rasteriser, the hit tester and Loaded reach the containers with no new case.
- `VirtualizingStackPanel : VirtualizingPanel : Panel`, as in Silverlight (StackPanel's layout is
  shared through `StackPanel.MeasureStack`/`ArrangeStack`). `VirtualizingPanel.ItemContainerGenerator`
  returns the owner's generator as `IItemContainerGenerator` (no members: WPR realises every
  container, so no panel can drive generation). `ItemsControl.GetItemsOwner` exists.
- A ListBox's `OnApplyTemplate` runs once, before its first measure (`WprBeforeMeasure`), with the
  built-in tree as its template root (`Control.SetBuiltInTemplateRoot`). A ListBox `Template` is
  stored, not drawn. `ScrollViewer.*ScrollBarVisibility` set on the ListBox reaches the ScrollViewer.
- Any ItemsPanel now lays a ListBox's containers out itself (Grid, the toolkit's WrapPanel).
- `VisualStateManager.GetVisualStateGroups` has Silverlight's signature,
  `IList GetVisualStateGroups(FrameworkElement)`, never null (LazyListBox's helper library binds it).

Fox News now draws its first page (the header bar), with no compat problem left. **Its content is
empty**, for two reasons that predate this change: the view model has no modules
(`FavoriteModules[0]` resolves to null), and the app's own bundled `Microsoft.Phone.Controls.dll`
Pivot throws NullReferenceException in `OnSelectedIndexPropertyChanged` (it expects its template parts
`PivotItemPresenter` / `HeadersListElement`). Regression (D3D11, base 961195e8 vs this): Gravity Guy
2, Mahjong, Minesweeper, Solitaire (the GG2 session's tap scripts), Carcassonne, Flowerz and Galactic
Reign reach the same screens; differences were the random deal, animation phase and run-to-run timing.

**YouTube (WP8, IdentityMine) reaches its first page** (step 3, patcher v53): the "trending" panorama
header. Its content is the 2013 YouTube API, which no longer answers. What it took, all general:

| gap | fix |
| --- | --- |
| `Frame.Navigated` was raised AFTER the page's `OnNavigatedTo` | now before, as on the phone (Navigating, OnNavigatingFrom, OnNavigatedFrom, Navigated, OnNavigatedTo, Loaded). YouTube's App takes the page's NavigationService in its Navigated handler and the page reads it in OnNavigatedTo; the other order was an NRE that killed the process. **The host does NOT use Navigated**: it lays the page out and applies its orientation from the WPR-only `Frame.WprNavigationCompleted`, raised after OnNavigatedTo (where Navigated used to be). On Navigated it saw Arkadium's game pages before they set their orientation, and Mahjong and Minesweeper drew their landscape boards into a portrait screen |
| ItemsControl ignored an observable `ItemsSource` | follows `INotifyCollectionChanged`: Add/Remove insert and remove only those rows, anything else rebuilds. A list bound to an ObservableCollection filled after a download stayed empty for ever |
| children of a collection object with no `[ContentProperty]` were rejected | they are its items (XAML's rule): YouTube's `TemplateTypeCollection` (a `List<TemplateType>`) was empty, so IdentityMine's ContentFrame had no template for any page |
| `{StaticResource}` could not see keys declared earlier in a dictionary still being parsed | `ParseContext.DictionaryScopes`: a standalone resources file (`datatemplates.xaml`) has no element above it. **Searched LAST**, only when element, template and application scopes all miss: consulted first it changed what Arkadium's menus resolved and every bound label on Mahjong, Minesweeper and Solitaire disappeared |
| a binding ON `DataContext` read the element's own DataContext | it reads the parent's, as in Silverlight. Before, set/clear fed back into each other until the stack overflowed. **With no parent context it leaves the value alone** rather than clearing it: Mashup sets a page's DataContext from code (in Silverlight that replaces the binding; WPR keeps it attached), and clearing it there blanked Fox News |
| `<X.Margin><Binding …/></X.Margin>` stored the Binding object | it binds the property, like the attribute form. Style setters whose Value is a Binding bind too, and a setter value of the wrong type is skipped (`[wpr-style]`) instead of poisoning the typed getter |
| `Type.GetProperty` threw `AmbiguousMatchException` for a property hidden with `new` and another type | `ReflectionMembers.GetProperty`, most-derived wins, in the reader and bindings |
| `DataTemplate.LoadContent()` returned `UIElement` | `DependencyObject`, Silverlight's signature |
| `JournalEntry.Source` read-only; `ShellTile.Delete()` returned bool | settable (GoBack navigates to it); void, and tiles are a real per-run set (Create adds, Delete removes, primary refuses) |
| `NavigationService` had no events | `Navigating`/`Navigated`/`NavigationFailed`/`NavigationStopped`, the frame's |
| missing types | WP8 `LongListSelector` (+ LayoutMode, ItemKind, ItemRealizationEventArgs), `ManipulationState`, `Microsoft.Phone.BackgroundTransfer` (real HttpClient transfers to and from the isolated store), `MediaLibraryExtensions` (WPR.Framework.Xna, WprFrameworkXnaTypes), WinRT `ApplicationProfile`, `VoiceCommandService.InstallCommandSetsFromFileAsync`, `Windows.System.Threading.ThreadPool` |

**WP8 `LongListSelector`** is built on the templated ListBox (ScrollViewer > ItemsPresenter >
[ListHeader, items, ListFooter]); rows are ContentPresenters. **`ItemRealized`/`ItemUnrealized` follow
the viewport (one screen either side)** because apps use them as "load the next page"; realising
every row at once would make them fetch page after page. Not built: grouping (flat), Grid layout
(laid out as a list), the jump list, ManipulationState (always Idle).

**Fox News shows its feed** (2026-10-22). The feed URL in its `app_config.json` is still live
(`feeds.foxnews.com/foxnews/latest` redirects to https); the 403s in the log are appmakr's dead
analytics pings. What stood between them was the WP7 toolkit's **Pivot, shipped in the app's own
Microsoft.Phone.Controls.dll** and so run as shipped. Each fix is general, and the first two reach
every WP7 title that ships that DLL:

| gap | fix |
| --- | --- |
| Pivot raises `LoadedPivotItem` (where Mashup starts a feed download) only from its page-change animation on the `PivotItemPresenter` template part, and no template was ever applied | `Control.EnsureTemplateApplied`: a control whose `OnApplyTemplate` is an app's or a shipped library's gets its template (its own, else the implicit style for its `DefaultStyleKey` in `themes/generic.xaml` of that type's assembly, `DefaultStyles`) **built but not drawn**, and the override called once, before its first measure **or Loaded**, whichever is first (Loaded is raised before layout here). WPR's own controls are skipped. Only the style's `Template` is used; WpChrome still draws a Pivot |
| `DefaultStyleKey`'s setter threw the value away | kept in the property store (the toolkit calls it on objects whose base chain here is not Control) |
| `TemplatedItemsControl.GetContainer` was null for every XAML item | `ItemsControl` prepares (`PrepareContainerForItemOverride`) the containers XAML declared, once, before the first measure or Loaded, as Silverlight's generator does |
| `int SelectedIndex` read null (metadata with only a callback) | such a value-type property defaults to `default(T)` |
| `Binding.Path` was a string; the Pivot's IL sets it as `PropertyPath` | `PropertyPath`, Silverlight's type (`PathText` internally) |
| no `WriteableBitmap(UIElement, Transform)` | the element's size, transparent (Render is still a no-op) |
| a path `FavoriteModules[0]` did not resolve, and a child whose own DataContext is bound was skipped when the parent's context arrived | indexers in binding paths (IList, or any one-argument int/string indexer; an observable collection on the way is watched); a DataContext binding is re-read when the parent's changes |
| `RssItemObservableCollection`'s non-generic `GetEnumerator` throws | items read through `IEnumerable<object>` when the source has it |
| its Add events arrive late through the dispatcher and one past their index: rows doubled | an Add is applied only if it turns the shown rows into the source as it is; otherwise the rows are rebuilt from the source, and an event the rows already reflect is ignored |
| `Control.IsEnabledChanged` was a field event; the toolkit's MenuItem (not a Control here) subscribes in its ctor, so the delegate overwrote the panel's children list | handlers kept in the property store, as DefaultStyleKey |
| `PhoneApplicationFrame.OrientationChanged` missing (ContextMenu) | declared; never raised, like the page's |

The rest of the log is harmless: `Activated` at a cold start (WPR's invention) throws
`KeyNotFoundException: RSS_MODULES` from `RestoreState`, after `Launching` already did the work.

GPU matrix, 2026-10-22 (first page, 30 s, scratch root):

| cell | Fox News | YouTube |
| --- | --- | --- |
| D1 D3D11 | ✅ the live feed (foxnews.com/latest), rows in feed order | ✅ "trending" header + banner |
| D2 Vulkan | ✅ same | ✅ same |
| D3 OpenGL | ✅ same | ✅ same |
| D4 GLES 3.0 + no DXT | ✅ same (washed out, as D4 always is) | ✅ same (washed out) |
| A1/A2, P1–P3 | not run (no Android build made for this) | not run |

Still open in YouTube (not chased): `ZmediaWrapper.VideoData.get_Duration` is missing from the
managed stub WinmdStubber makes of the app's native `ZmediaWrapper` WinRT component, and
`Windows.Phone.Apps.UpdateServiceClient.CheckAsync(bool)` is missing although the app ships that DLL
itself (a by-name redirect is the likely cause; both are the stub-policy question, not shim work).
`<FontFamily x:Key>` resources do not resolve: there is no `FontFamily` type, and `TextBlock.FontFamily`
is a string here. Library blast radius of `get_MergedDictionaries`: no other non-Unity title calls it.

### A WP7 Silverlight game quits by THROWING, and WPR was swallowing it (2026-09-24)

WP7 Silverlight had no public exit API. The universal idiom is therefore:

```csharp
public static void Quit() => throw new QuitException();   // a private type
// ... Application.UnhandledException:
//     if (e.ExceptionObject is QuitException) Application_Closing(null, null);
//     and DELIBERATELY no e.Handled = true, so the shell terminates the app.
```

WPR declared `Application.UnhandledException` and **never raised it**, so the throw met the game
loop's catch-all, was logged through `WprDebugTrace` — which is `[Conditional("DEBUG")]` — and
vanished. In a Release build that is a completely silent no-op. **The symptom is a quit
confirmation whose "Yes" does nothing**, reported against Cut the Rope.

**This only became visible when Back started reaching the game.** Before the predictive-back fix
(see the manifest note) the system finished `GameActivity` itself, so Back always ended the game
and nothing ever asked the game to quit. Fixing Back exposed a second, older gap behind it — worth
expecting whenever a regression fix restores a path that had been dead.

**The catch that matters is NOT the one in `Game.Tick`.** A mixed-mode title's update runs under
`GameTimer.RaiseUpdate`, which has its own `try`/`catch` around the `Update` and `FrameAction`
handlers, so the loop's catch never sees the exception at all. Hooking only `Game.cs` changes
nothing — measured. Both sites now report to one place.

**The shape**: `WPR.Xna.Rhi.GameUnhandledException` holds a `Reporter` slot and a
`TerminationRequested` flag; `WPR.WindowsCompability.Application`'s constructor fills the slot and
`ResetCurrent` clears it. Every catch calls `Report`, and `Game.Tick` acts on the flag once per
tick. A flag rather than an immediate `Exit()` because none of the catch sites holds the `Game`,
and because it guarantees the game's handler is raised exactly once per throw and there is exactly
one `Exit`.

The slot lives in `WPR.Framework.Xna` because `WPR.Framework.Silverlight` references it and not the
reverse — the same constraint that produced `XamlReader.ApplicationResourceLookup`. It names only
`Exception`, so no vocabulary leaks either way.

**Three gates, and each one exists to stop this becoming a behaviour change for everything else:**

- **No reporter → swallow.** Only a Silverlight or mixed-mode title constructs an `Application`, so
  a pure XNA game is untouched. That matters: several titles throw out of `Update` on every frame
  and stay playable or at least diagnosable precisely because WPR swallows it (Feed Me Oil,
  Chickens Can't Fly, Brain Challenge).
- **No `UnhandledException` subscriber → swallow.**
- **Subscriber left it unhandled, but the exception type is NOT defined by the game → swallow.**
  This one is the important one and is not obvious. The stock WP7 project template subscribes the
  event with a body that does nothing but `if (Debugger.IsAttached) Debugger.Break();`, and
  **all twelve** Silverlight titles installed here carry that subscription — so honouring WP7
  literally would let one stray `NullReferenceException` start closing games that currently limp
  along. A quit is always signalled with a *game-defined* exception type (you cannot express "the
  user chose to exit" with an NRE), so that is the discriminator. It is tested by load context —
  game code lives in `ApplicationLaunch`'s own `AssemblyLoadContext` — which beats matching names,
  and anything uncertain answers "not the game", i.e. swallow.

**Read it out of the log.** `[wpr-quit]` lines go through `XnaBackend.LogInfo`, so unlike the rest
of this path they survive a Release build — without them "the game asked to quit and we ignored it"
and "nothing was thrown" are indistinguishable:

```
[wpr-quit] ctre_wp7.App+QuitException escaped the game loop; unhandled — exiting, as WP7 would.
```

Shim behaviour, so no `ApplicationPatcher.Version` bump and no reinstall; games pick it up on next
launch.

### The Silverlight overlay is rasterised on the CPU (2026-09-22, patcher v32)

A mixed-mode title's page is a real Silverlight visual tree, and the game gets at it through
`UIElementRenderer`: it rasterises that tree into a `Texture2D` the game then blits with its own
`SpriteBatch`. WPR's was a stub that handed back a correctly-sized transparent texture — enough to
turn a `TypeLoadException` at page construction into a game that runs, and no more.

**The overlay is not always an overlay, which is why "transparent" was not good enough.**
Rabbids Go Phone's `GamePage.OnDraw` draws it *before* `screenManager.Draw`, so for that game it is
the **background**: its entire main menu — the blue scribbled city — is one `ImageBrush` on the
page's root `Canvas`, set from `RenderBlueBG`. The 3D logo and labels drew perfectly on black, which
reads as a texture or blend-state fault and is neither. **Two games composite this texture in
opposite directions; do not assume either.**

`SoftwareVisualRasteriser` (`Src/Core/WPR.Framework.Silverlight/`) is the painter. Four things
about it are deliberate:

- **CPU, not Avalonia.** No UI framework is involved, which is what lets these titles run on
  Android at all. It began as a second renderer beside the Avalonia `SilverlightRenderer`; since
  Stage D (2026-10-13) it is the only one.
- **Output is premultiplied RGBA packed as XNA's `Color`** (R in the low byte). Games blit it with
  a default `SpriteBatch.Begin()`, which is `BlendState.AlphaBlend`, i.e. premultiplied; a
  straight-alpha buffer fringes every edge.
- **It paints only what the tree asks for**, with no page backdrop (the deleted Avalonia
  renderer filled black first). An unrequested fill here would hide
  the game rather than the missing overlay.
- **Text is not drawn.** It needs a glyph rasteriser, which is a different and much larger job.
  Gradients, `RenderTransform` and popups are also absent. Every unsupported thing it meets is
  reported once by name as `[wpr-uirender]`, so a blank region has a reason in the log instead of
  being indistinguishable from a bug.

**`Render()` runs once per frame from a game's draw, so it is gated twice.** First a hash of
everything the rasteriser would look at (`SoftwareVisualRasteriser.Signature` — rects, visibility,
opacity, brush identity, plus a decode generation counter); then a comparison of the composed
pixels against what was last uploaded. A WP7 page is static in the steady state, so this costs a
walk of a handful of nodes and no GPU traffic. Without the first gate a full-screen
clear-and-recomposite would run sixty times a second for ever. **The generation counter is not
decoration**: when an image finally resolves, nothing in the tree changes, so without it the frame
that could first paint the picture would compare equal and never be drawn.

**The image is usually not a file, and that is the part that catches you out.**
`ImageSource="LoadingScreen.png"` names an entry in `RabbidsGoPhone.g.resources` — the Silverlight
`Resource` build action — and there is no such file anywhere in the install.
`SilverlightImageDecoder` therefore tries, in order: the path as given, the path under
`HostContext.CurrentInstallFolder` (separators normalised, the `ContentPaths` trap), and then
`Application.GetResourceStream` against `HostContext.UserAssembly`, which already knows both a
plain manifest resource and a `.g.resources` bundle entry. A resolver that only looked at the
filesystem finds nothing and the page paints empty, which looks exactly like "this brush is not
supported yet".

Two things about that decoder are load-bearing rather than tidy:

- **It decodes through `BitmapSource.SetSource`**, so there is one image-decode path in the
  assembly and, when the source really is a `BitmapSource`, the pixels land on the game's own
  object — a title that later reads `PixelWidth`/`Pixels` off the bitmap it handed us gets real
  numbers instead of the 1x1 placeholder.
- **`ResetForNewLaunch` is required for correctness, not just to give memory back.** The path cache
  is keyed by the relative name a page asked for, and two games share one process on the desktop
  head, so without it the next title's `LoadingScreen.png` would be the last one's.
  `ApplicationLaunch` calls it beside the other singleton resets.

**`UIElementCollection` had to gain a base class, and that was a real latent bug.** Silverlight
declares `Add`/`Clear`/`Remove` on `PresentationFrameworkCollection<UIElement>` and
`UIElementCollection` redeclares none of them, so ordinary game code — `LayoutRoot.Children.Add(x)`
— compiles to a callvirt on `PresentationFrameworkCollection<UIElement>::Add`. WPR's was a bare
`IList<UIElement>`, so that call had nowhere to land. The remarks on the base class already warned
that concrete collections must really inherit from it; this one did not. Verified afterwards that
Minesweeper's panorama dashboard still builds, since every Silverlight title goes through this.

**`System.Windows.Controls.ProgressBar` and its `RangeBase` are the patcher change (v32).** A WP7
loading screen routinely builds one, and a missing type resolves when the method naming it is
*compiled* — so the whole of `RabbidsHD.Screens.Loading.LoadContent` failed, the screen was never
added, and tapping **My Rabbid** on the main menu did nothing with nothing on screen to say why.
The properties live on `RangeBase` because that is where Silverlight declares them: `bar.Value = 50`
compiles to `callvirt RangeBase::set_Value`, and a hierarchy that puts the setter on the wrong
class does not resolve at JIT time. The chrome classifier (`WpChrome`, then also the Avalonia
`SilverlightRenderer`) matches our shim's own FullName as well as the two WP7 ones — matching only
the WP7 name would have stopped recognising it the moment the type existed.

**Not identity-binding, so `--repatch-installed` is enough and nothing needs a reinstall.** The
rasteriser itself is shim behaviour and needs neither.

**How to recognise this class of fault.** A mixed-mode title that renders its own content
correctly on a flat black (or otherwise empty) ground is an unrasterised Silverlight layer, not a
renderer bug — check whether the page has a `Background` brush before suspecting the GPU. And a
menu that takes taps and does nothing is, as ever, a missing member inside the handler: dump the
failing method's member references with Cecil and look for the ones still scoped to
`System.Windows` or `Microsoft.Phone`.

Verified on Windows: Rabbids Go Phone menu with its background, into **My Rabbid** and a live 3D
Rabbid; Sid Meier's Pirates! now paints its 2K Games logo page, which was blank; Cut the Rope and
Little Acorns unaffected (Little Acorns reaches a level).

### Who draws a mixed-mode page: ask what was PAINTED, not what subscribed (2026-09-25)

`SilverlightGame.Draw` composites the Silverlight page only when **nothing else painted the frame**.
Two things can already have accounted for it, and neither may be drawn over: the game may have
rasterised that very tree itself (`UIElementRenderer.GameCompositedThisFrame`), or it may have
painted a scene of its own. Otherwise the screen would be empty, so the host draws the page.

**The test used to be `GameTimer.AnyDrawSubscriber`, and one title defeats it outright.** Galactic
Reign (`45859ddf-684e-43bc-a282-0a4494e88864`) creates its `GameTimer` inside a process-lifetime
`RenderManager`, subscribes `Draw` and calls `Start()` **in the constructor** — so the proxy
answered "this page draws itself" on every page of the game forever, while `RenderManager.Draw`
returns immediately unless its own unrelated `IsRunning` flag is set. Nobody drew the menu and the
screen stayed black with a completely clean log: page constructed, navigated, laid out at 800x480,
zero draw calls. **Nine of the ten mixed-mode titles subscribe from a specific page's constructor**
(`GamePage::.ctor`, `IngamePage::InitializeXNA`, `ModeGame::.ctor`…), which is why the proxy held
so long — it is a property of how a title happens to be written, not of the platform.

**`GraphicsDevice.WprDrawCallsThisFrame` is the replacement**, sampled either side of
`GameTimer.PumpDraw`. It is live in Release — only the tracing around the counter is
`[Conditional("DEBUG")]`, not the count. Clears deliberately do not count: a game that clears and
draws nothing has not put a frame on the screen, which is exactly Galactic Reign's menu.

**DO NOT widen this to "composite whenever the game did not rasterise it".** WP7's compositor
really did put the Silverlight tree over the shared device's output every frame, so the faithful
rule looks obviously right — it was tried on 2026-09-25 and **it broke titles that were correct**:
Sonic lost sprites, Cut the Rope lost its finger. A page WP7 would have shown as a transparent
sheet is, through this rasteriser, a full-screen blit whose painted parts hide the game beneath.
Faithful compositing needs the page to be faithful first. Until then the host draws the page only
when the alternative is an empty screen.

**The safety check that missed it is worth remembering too.** The first attempt measured the
overlay's *fully opaque* pixel fraction and reported `0.0%` for Cut the Rope, Cut the Rope Exp and
Little Acorns — which reads as "harmless" and is not: alpha 254 covers the game just as
effectively as alpha 255. **Measure painted coverage, not opaque coverage**, if you ever need this
number again.

**Presentation and input must be decided by the same test.** `PumpSilverlightTouch` runs in
`Update` and so latches the previous `Draw`'s answer (`_hostCompositesPage`) rather than asking
again. They were briefly decided by different tests, which would have left Galactic Reign's menu
visible and dead to the touch — a gating bug wearing a hit-testing bug's clothes.

Read it out of the per-game log; one line per change of answer, because a title whose menu is
plain Silverlight and whose board is XNA flips it on every navigation:

```
[wpr-mixed] nothing else painted this frame — compositing the page.
[wpr-mixed] the game painted this frame — not compositing.
[wpr-mixed] the game rasterises this page itself — not compositing.
```

Measured over all ten installed mixed-mode titles: Carcassonne and Galactic Reign composite;
Cut the Rope, Cut the Rope Exp, Little Acorns, FC Rocket, Big Buck Hunter Pro, The Game of Life
and Pirates do not. **That distribution is the regression test** — a change here that moves a
title between those two lists needs a screenshot, not a draw count.

No `ApplicationPatcher.Version` bump and no reinstall — host behaviour, picked up on next launch.

### The rasteriser turns and shears by rendering upright and resampling (2026-09-25)

`SoftwareVisualRasteriser` handles `RotateTransform`, `CompositeTransform.Rotation` and skew.
Anything that turns or shears takes a different path from the axis-aligned one: the element is
rendered **upright into an offscreen buffer** and then inverse-mapped, bilinearly, into the
destination. That is what makes it one code path rather than one per primitive — teaching every
fill, blit and glyph run to walk a rotated edge is the cost estimate that kept this unimplemented.

Four things are load-bearing:

- **`TryGetTransform` is untouched and still the path every element takes.** It reduces a
  transform to a scale and a translation and is exact for those. `TryGetRotationSkew` is a
  *second* walk of the same chain that runs only once a rotation or skew is present, so the
  general affine — and its ordering rules — stay off the hot path.
- **Order matters here and does not there.** Scales multiply and translations add however they are
  arranged, so the axis-aligned path can ignore order; a rotation cannot. `Compose` follows
  Silverlight's: inside a `CompositeTransform`, scale → skew → rotate about `CenterX/CenterY`,
  then translate; inside a `TransformGroup`, children in declaration order.
- **A non-uniform ancestor scale must stay on the OUTSIDE.** The device-space linear part is
  `S·M·S⁻¹` for `S = diag(scaleX, scaleY)`, which for a diagonal `S` touches only the off-diagonal
  terms. Skip it and a rotated element shears whenever its ancestors scaled the axes differently.
- **The change-detection signature must include the rotation.** `Signature` hashes
  `TryGetTransform`'s output, and a *pure* rotation has neither scale nor translation — so without
  a separate contribution a spinner animating only `RotateTransform.Angle` hashes identically every
  frame and is painted exactly once, at whatever angle it started on. That looks precisely like
  rotation never having been implemented.

**Sampling is bilinear over PREMULTIPLIED pixels**, which is what makes a plain per-channel lerp
correct: interpolating straight-alpha colour pulls the colour of fully transparent texels into the
edge and fringes every rotated element. Same property that makes the rasteriser's output
premultiplied in the first place.

**The buffer is the element's own slot, so a child painting outside its parent's bounds is clipped
here where the axis-aligned path would have shown it.** Silverlight does not clip to bounds, so
that is a real difference rather than a rounding of one, and it is bounded that way on purpose —
the alternative is a margin constant chosen to hide a case rather than to describe one. If a title
ever needs more, union the subtree's arranged rects. An element larger than
`MaxTransformPixels` (four WVGA screens) falls back to drawing upright rather than being dropped.

**`scratchpad/rotprobe` is the regression test** — 11 checks, no game and no screen: extents and
centres for 90°/180°, area preservation, the default origin as a fixed point, skew width against
`tan`, that the signature tracks the angle, and that **rotate-then-translate differs from
translate-then-rotate** (the one that catches a matrix composed in the wrong order, since the
axis-aligned path cannot tell them apart and would agree with a wrong answer).

With this in, the `[wpr-uirender]` unsupported list is **empty across all ten installed mixed-mode
titles** — no shapes, popups, brushes or missing fonts reported on the screens they reach.

Shim behaviour, so no patcher bump and no reinstall.

### The rasteriser draws real shapes (2026-10-12, patcher v41)

`Ellipse`, `Line`, `Polyline`, `Polygon` and `Path` used to be drawn as their bounding boxes —
and only `Rectangle`/`Shape` existed as types at all, so a `Path` in game IL was a
`TypeLoadException`. All five now exist, with the whole geometry family (`PathGeometry` +
figures/segments incl. arcs, `EllipseGeometry`, `RectangleGeometry` with radii, `LineGeometry`,
`GeometryGroup`), the path mini-language (`PathMarkupParser`, through `XamlTypeConverter`), and
`Points`/`StrokeDashArray` strings. Painting is `SoftwareVisualRasteriser.Shapes.cs` +
`.Geometry.cs`; the main file only dispatches.

How it works, so nobody re-derives it:

- **Flatten in LAYOUT space, after `Stretch`, then stroke, then map to device.** The pen is not
  stretched (Silverlight's rule), but an ancestor's non-uniform scale does stretch it, and stroking
  before the device map gets both right. Curves are transformed before flattening, at 0.15 device
  px, so zooming never shows facets.
- **One coverage rasteriser** (`RasteriseCoverage`): 16 sub-scanlines per row, exact horizontal
  span coverage, fill rule evaluated per sub-scanline — so EvenOdd is exact. A stroke is a union of
  pieces (segment quads, joins, caps) each normalised to positive winding and filled Nonzero; skip
  the normalisation and overlapping pieces of opposite winding cancel into holes.
- **Shapes are clipped to the ancestor clip, not their slot**: a `Stretch="None"` path draws at its
  own coordinates, routinely outside the box layout gave it. `UniformToFill` is the exception.
- **A plain rectangle keeps the old exact pixel path** (no radius, no dash, Stretch None/Fill), so
  WP7 chrome is not softened. Measured: Carcassonne's menu and Galactic Reign's first screen are
  byte-identical before and after, and the composite/no-composite answers did not move.
- **Non-solid brushes** are painted by `FillBrush` into a scratch buffer the size of the coverage
  mask, so gradients and image brushes work in shapes for free.
- `Signature` hashes the geometry point by point, stroke style and dashes included.

**`Path` is now a type in `WPR.SilverlightCompability`, which shadows `System.IO.Path`** for any
code in that namespace (namespace members beat usings) and makes a bare `Path` ambiguous in files
that import both. Write `System.IO.Path` inside the shim assembly; elsewhere add
`using Path = System.IO.Path;` (done in `SilverlightAppHost` and `ApplicationLaunch`).

Verified by `scratchpad/rastshapes` (scenes rendered through the rasteriser and through an
independent SkiaSharp reference that re-parses the path strings itself: under 1% of pixels differ by
more than 48/255, all at anti-aliased edges and dash phase) and `ShapeRasterTests` (coverage, both
fill rules, area of an ellipse/arc/ring, stroke width, caps, joins, stretch, signature). Flowerz's
own `LevelAnimation`/`Explosion` controls (150 Paths, rotated petals with 50% shadows) render
correctly through `scratchpad/slpage`, which boots a pure-Silverlight title from a repatched SCRATCH
COPY of its install. Its `GameBoard` page could not be built then (`MouseButtonEventHandler` had no
rescope entry); fixed in Stage C, below.

Not done: `StrokeDashArray` phase restarts per figure; miters past the limit are bevelled rather
than clipped.

**Patcher table change (v41)**: the shape/geometry types gained rescope entries. Not
identity-binding — `--repatch-installed` on the desktop, automatic on Android.

### Pure Silverlight titles run on the XNA host (Stage C, 2026-10-13, patcher v42)

`ApplicationLaunch` no longer throws "Silverlight UI runtime is not yet implemented": every
`ApplicationType.Silverlight` title is hosted by `SilverlightGame` (CPU rasteriser, `TouchPanel` →
`SilverlightTouchRouter`). That throw was the **only** thing keeping them off Android. (The
Avalonia host stayed behind a flag at this point; Stage D, next section, deleted it.)

**Default is the XNA host, measured, not assumed** — both hosts driven by the same script
(`scratchpad/gamerun` for XNA, `scratchpad/slav` = the Avalonia host headless, rendering
`PhoneApplicationFrameView` with Avalonia.Headless + Skia), both against a **scratch data root**
(`WPR_DATA_ROOT`, prepared by `scratchpad/slprep`, which copies and repatches installs and never
writes the user's):

| title | Avalonia host | XNA host, desktop | XNA host, Android (`WPR_Verify`, Release APK) |
| --- | --- | --- | --- |
| Flowerz | menus as **text only** (it cannot draw a `BitmapSource.SetSource` image), board reachable, **taps on the board do nothing** (no `MouseLeftButtonDown` on that host) | images, menus, board, pieces placed, queue counts down — D1–D4 | same, A1 (GLES 3.1) and A2 (Vulkan) |
| Minesweeper | dashboard, panorama swipes to powerups / classic / speed | **identical** pictures — D1–D4 | dashboard → swipes → classic, A1 and A2 |
| What In The World? | dies on start-page navigation (now: on `VoiceCommandService`) | same failure, same place | not run |

Android: install a desktop-patched copy plus a catalogue row (the memory note on the emulator), held
presses for taps, `fna3d_driver.txt` per cell; Carcassonne (mixed-mode, repatched on device to v42)
still composites its menu on A2. Not run: S24 (P1/P2), Mali (P3).

Neither host gets further into Minesweeper: its own guard `MainPage.c()` swallows every dashboard
and mode-list tap (a popup / full-screen-ad / 1.2 s debounce check), on both. What In The World? is
WP8 managed and stops in WinRT (`VoiceCommandService.InstallCommandSetsFromFileAsync`,
`CurrentApp.AppId`, `InstallationManager`), as an unhandled exception on both hosts.

**What the XNA host needed, all framework fixes shared by both hosts** — each is a pattern other
pure Silverlight titles will hit:

- **`GetResourceStream` reads files in the package.** A relative URI without `;component/` names a
  XAP *file* (the Content build action). It only searched embedded resources, so Flowerz's strings
  and images were null and its loading page sat black on **both** hosts. It also fixed **Big Buck
  Hunter Pro** in the headless harness (its `AppResourceUtils.LoadFileStream` → black screen
  before; menu now). Last pass, so an embedded resource of the same name still wins.
- **Loaded on the root, and on elements that join a live tree.** Flowerz starts from
  `AppFrame.Loaded`; its bloom animation starts from its own Loaded when added to a canvas, and the
  game waits for that storyboard before drawing a placed piece. `UIElement.SetParent` queues the
  child when the parent is loaded; `FrameworkElement.RaisePendingLoaded` (host, once a frame)
  raises it. **Pure Silverlight only in `SilverlightGame`** — a mixed-mode title's queue is dropped
  unraised, which is what it always saw.
- **Re-layout on invalidation** (pure only): Flowerz fills its score/level TextBlocks in
  `OnNavigatedTo`, *after* the page swap that triggered layout, so they were arranged at width 0.
  `RelayoutIfInvalidated` polls the page's `IsMeasureValid` before compositing.
- **`Frame` honours `ContentControl.Content` written on it.** Silverlight's Frame *is* a
  ContentControl and Flowerz swaps every page with `((ContentControl)this).Content = page`. Ours
  derives from FrameworkElement, so the value went into the property store and nothing changed.
  `ContentControl.OnContentChanged` now routes a Frame to `ReplaceContentDirectly`
  (`WprContentReplaced` event, no `Navigated`, Loaded raised).
- **`FrameworkDispatcher.Update` off the game thread no longer touches `TouchPanel`** and no longer
  counts as the app's pump. Flowerz and Minesweeper pump it from a `BackgroundWorker` every 10 ms
  (the WP7 idiom for XNA audio in a Silverlight app); that raced the game thread's pump and a tap
  could press and release between two frames — Minesweeper ignored every tap. "Game thread" is
  `OffThreadGpuCalls.OnDeviceThread`, so a game that pumps before creating a device is unchanged.
- `MouseButtonEventArgs : MouseEventArgs` (GetPosition lives on the base, and Flowerz casts to it);
  `UIElement.MouseMove` (raised along the press chain); patcher **v42** rescopes
  `MouseButtonEventHandler`/`MouseEventArgs`/`MouseEventHandler`, whose shims existed with no entry.
- `ListBox : Selector` (selection moved to Selector; Minesweeper casts `(Selector)sender`), and a
  tap selects the row before Tap handlers run. Panorama/Pivot horizontal pan + page commit ported
  from `PhoneApplicationFrameView` into `SilverlightTouchRouter`.
- `PropertyPath(string, params object[])` (every code-built storyboard in Flowerz),
  `XamlReader.Load` strips a BOM, image paths clamp leading `..` at the package root, WP8
  `IsNavigationInitiator`, `NameResolutionCallback`/`ResolveHostNameAsync(DnsEndPoint…)` (answers
  synchronously: the caller waits 50 ms), a silent `BackgroundAudioPlayer`.

**Mixed-mode distribution, measured with the harness idle for 25 s, combine HEAD vs this change**:
identical for nine titles; **Big Buck Hunter Pro moved from "compositing" (a black screen, its
start page failed on the missing file pass above) to "the game painted"** — which is what the
"Who draws a mixed-mode page" list already says it should be. The Game of Life composites in an
idle run on both builds (it sits on its title page); the list above was measured further in.

**Three harness traps worth knowing.** gamerun's SDL window sits on the real desktop with
`MouseAsTouch` on, so **a real click lands in the game** — scripted runs now move it off-screen
(`GAMERUN_ONSCREEN=1` to keep it); turning `MouseAsTouch` off instead stops `TouchPanel` updating
and the script with it. Neither harness composes a `Guide` message box, so a title that asks a
question (Flowerz's "new game?" when a save exists) stalls there — `sl-run.ps1` clears the scratch
store each run. And neither harness has audio on the Avalonia side (`NoAudioHardwareException` out
of every Flowerz button click there): `AudioBackendRegistry`'s base is set only by `FnaGameHost`,
so the **real** Avalonia host has the same problem.


### One Silverlight host: the Avalonia one is deleted (Stage D, 2026-10-13)

`SilverlightGame` (`Src/Engine/WPR.Engine.GameLoop/`, **renamed from `MixedModeGame`**) is the only
Silverlight host, on both heads. Gone: `SilverlightLauncher`, `PhoneApplicationFrameView`,
`SilverlightRenderer`, `BrandedSplashRenderer`, `IBackgroundRenderer`, `PhoneHardwareButtons` (the
desktop bezel), `SilverlightHostChoice` with the `WPR_SILVERLIGHT_AVALONIA` variable and the
`SilverlightAvaloniaHost` config.json key (an old config.json still carrying it loads fine:
Newtonsoft ignores unknown keys), and `MixedModeDetection` with the `isMixedMode` routing in
`ApplicationLaunch` and `MainWindowDesktop`. About 2,400 lines net.

- **`WPR.Framework.Silverlight` references no UI framework.** Its Avalonia package, and with it the
  11.3.9/12.1.3 split *inside* that project, is gone; the measured cost of moving the desktop to
  Avalonia 12 now sits on `AvaloniaVersion` in `WPR.Platform.Windows.csproj`. **The APK still
  carries Avalonia**: the Android head references `Avalonia.Android` 12.1.3 for its AppCompat theme
  resources, so the APK did not shrink (Release, 208,486,666 bytes).
- **The mixed/pure distinction survives, inside the host.** It is behaviour, not routing: Loaded on
  the root tree and on late-added elements, and re-layout on invalidation, are pure-only, and the
  ten mixed-mode titles' composite/no-composite split depends on them staying off.
  `SilverlightGame.NamesSharedGraphicsDeviceManager(assemblyPath)` is the old detector's test (a
  `SharedGraphicsDeviceManager` type reference in the main assembly), now over
  `System.Reflection.Metadata`. Log: `[wpr-silverlight] pure Silverlight title …` /
  `… mixed-mode title (SharedGraphicsDeviceManager)`.
- **GameMaker exports have their own launch path.** They only ever reached YoYo's Runner.exe
  from inside `SilverlightLauncher`. The desktop now asks `GameMakerLauncher.TryLaunchAsync`
  (WPR.Runtime) after `UnityPortLauncher` and before `XnaLauncher`; it answers false when there is
  no `Assets/game.win` or no Runner, and the title then goes to `SilverlightGame`. `RequestExit`
  kills the Runner when WPR closes. Reported to the hub as runtime path `other` (the hub's list has
  no GameMaker value). **Unverified**: no GameMaker title or runtime is installed here.
- `DrawingSurfaceBackgroundGrid` is a plain Grid (background + children); `BingWallpaper` keeps only
  the file the rasteriser decodes. A Silverlight install now reports runtime path `xna-fna`.
- No patcher change (still v42): nothing renamed is bound by game IL.

Measured on the headless harness against the scratch data root, `combine` vs this change, same
scripts: the ten mixed-mode titles keep their split exactly (Carcassonne, Galactic Reign and The
Game of Life composite in an idle run; the other seven do not), with pixel-identical frames for the
static ones; Flowerz (menu → board → pieces placed) and Minesweeper (dashboard → panorama → classic)
reach the same screens on D1, and on D2–D4 after the change. Android was not run (no emulator free).

### The WP Toolkit's own gesture engine runs (patcher v50, 2026-10-20)

A game that ships `Microsoft.Phone.Controls.Toolkit.dll` now runs that Toolkit's `GestureService` /
`GestureListener` as written; WPR implements what it calls underneath. 13 library packages ship the
Toolkit; Carcassonne, Ice Age Village, Minesweeper and Sudoku use its gestures.

**The old redirect never worked.** The patcher pointed the gesture types at WPR's `Microsoft.Phone`
with a fresh `AssemblyNameReference` that was not the module's own `Microsoft.Phone` ref, so Cecil
wrote those typerefs **scoped to the game module itself**, which defines none of them (check with
Cecil: `GestureEventArgs -> Minesweeper.dll`). The XAML reader meanwhile resolved
`toolkit:GestureListener` to WPR's, so every `Tap="handler"` failed to bind (`[XamlReader] SKIP attr
'Tap="m"'` in stdout) and the listener had no subscribers. **That was the Minesweeper/Sudoku "guard"
Stage C recorded**: menu taps never reached the game at all. And WPR's listener only ever raised
Tap, so Hold/Drag/Pinch/DoubleTap reached nobody.

What the Toolkit needs, and what changed to give it:

| Toolkit call | was | now |
| --- | --- | --- |
| `Touch.FrameReported` | declared, never raised | `TouchFrameReporter.Report` from `SilverlightGame.PumpTouchFrameReported` |
| `TouchPoint.Action != 3` | WPR's enum was 0/1/2 | Silverlight's `Down=1, Move=2, Up=3` |
| `VisualTreeHelper.FindElementsInHostCoordinates` | always empty | `HitTester.HitTest` |
| `TransformToVisual(...).Inverse`, cast to `MatrixTransform` | identity, no `MatrixTransform` type | translation from `HitTester.OriginInRoot`; `Matrix`/`MatrixTransform` exist (patcher entries) |
| `DispatcherTimer` (its 100 ms gesture poll) | ticked on a thread-pool thread | ticks on the game thread |
| `TouchPanel.ReadGesture` | already real | unchanged |

Things that are load-bearing:

- **FrameReported is raised AFTER the router's pass**, not before. A list selects the tapped row
  on release, and the Toolkit raises Tap from this event; first, Tap reached the handler with
  `SelectedItem == null` and Sudoku's mode list ignored it.
- **It is raised whoever draws the page**, before `PumpSilverlightTouch`'s "game reads touch" gate,
  and the read counter is resynchronised after it: the Toolkit's own `IsGestureAvailable` reads
  would otherwise count as the game reading touch and stop routed input on a page the game draws.
  Reads from its 100 ms timer during a touch still count.
- **A finger that vanishes without a Released sample gets an Up**: the Toolkit ends a gesture only
  on a frame whose points are all Up; otherwise its timer and hit list live for ever.
- **`DispatcherTimer` now ticks on the game thread for every Silverlight title** (one queued tick at
  most, dropped after `Stop`/restart), as Silverlight did on its UI thread. `Stop` is thread-safe:
  Sudoku stops a timer from a `BackgroundWorker` completion, and the old check-then-dispose raced
  into a NullReferenceException that killed the process (reached only once taps worked).
- **XAML**: an xmlns naming `assembly=Microsoft.Phone.Controls.Toolkit` is looked up there first
  (`XamlReader.ToolkitAssemblyName`). WPR's own `GestureService`/`GestureListener` stay for installs
  not yet repatched; the router still dispatches to them.
- **`TransformToVisual` agrees with hit testing by construction** (`OriginInRoot` walks the same
  ArrangedRect / ScrollViewer / PanoramaItem rules). RenderTransforms are in neither.

**`Microsoft.Phone.Shell.ApplicationBar` exists now, stored but not drawn** (`IApplicationBar`,
`ApplicationBar`, the icon button / menu item types, `PhoneApplicationPage.ApplicationBar`). It was
missing entirely, so every page declaring one (Minesweeper's and Sudoku's boards) failed to
construct and the navigation to it silently did nothing. Drawing the bar and routing taps to its
buttons (`ApplicationBarMenuItem.RaiseClick`) is still to do.

Verified 2026-10-20, desktop D3D11 harness, scratch data roots: Minesweeper and Sudoku menus take
taps (Achievements / Help & Options / Leaderboards open, Back returns, Sudoku Training opens its
board, the tutorial's ok dismisses). A/B against the branch tip without this change, same scripts,
12 titles (Carcassonne, Galactic Reign, AlphaJax, Flowerz, Minesweeper, Cut the Rope, Little
Acorns, Big Buck Hunter Pro, The Game of Life, Pirates!, Rabbids Go Phone, Sudoku): identical
composite split, zero thrown frames or crashes on either side, frames identical apart from startup
timing and animation noise; Carcassonne's Leaderboards and Options buttons open the right pages.
Shim tests 269/269 (`ToolkitGestureSurfaceTests`). **Not verified**: Hold/Drag/Pinch in play.
Minesweeper's and Sudoku's boards sit behind intro panels driven by visual states and
`PlaneProjection` flips WPR does not animate, so the cells were never reached. Not run: D2–D4
(nothing here draws), Android, any phone.

**Patcher table change (v50; written as v47 on branch toolkit-gestures).** Not identity-binding: `--repatch-installed` on the desktop,
automatic on Android.

### When I touch a shim type

Two distinct rebuild paths depending on what changed:

1. **Shim implementation only** (`WPR.Framework.Silverlight/*.cs`,
   `WPR.Framework.Phone/*.cs`, `WPR.WindowsCompability/*.cs`,
   `WPR.Framework.Xna/*.cs` (including its `Compat/` overrides), GamerServices,
   etc.): just rebuild — installed games will pick up the new behaviour on
   next launch because they reference the shim assembly, not a snapshot of it.
   **No reinstall needed.**
2. **Patcher table change** (`Src/Core/WPR.Loader/ApplicationPatcher.cs` — adding
   entries to `Patches` / `MemberPatches` / `WprFrameworkXnaTypes`, changing target
   types): rebuild **and** **reinstall the affected games**, and bump
   `ApplicationPatcher.Version` so the installer knows the IL is stale. The IL was
   rewritten at install time; adding a new redirect now does nothing to
   already-installed `.dll`s.

   Note `WprFrameworkXnaTypes` (the set rescoped to `WPR.Framework.Xna`) is tested
   **before** `Patches`, so a FullName in both silently loses its `Patches` redirect.

The common "add a new shim type" task is **both**: add the shim class, add the
patcher entry, rebuild, reinstall the affected game.

### Shim file layout (project `WPR.Framework.Silverlight`)

**Project name ≠ namespace.** Stage 3 renamed the *project* to
`WPR.Framework.Silverlight`, but the code inside was deliberately left alone: every
file still declares `namespace WPR.SilverlightCompability`, and that is what the
patcher redirects to (`NewNamespace` in `ApplicationPatcher.cs`). Don't "fix" the
namespace to match the folder — you'd have to rewrite the patcher tables and reinstall
every game.

This project's source tree mirrors the real Silverlight namespace hierarchy as
directories — **one C# class per file, file path matches where the type lives
upstream**. The directory structure is pure organisation; the assembly is one flat DLL
in one flat namespace regardless of where on disk a file sits.

Examples:
- `System.Windows.Shapes.Rectangle` → `System/Windows/Shapes/Rectangle.cs`
- `System.Windows.Controls.Primitives.Popup` → `System/Windows/Controls/Primitives/Popup.cs`
- `System.Windows.Media.Animation.Storyboard` → `System/Windows/Media/Animation/Storyboard.cs`
- `System.ComponentModel.DesignerProperties` → `System/ComponentModel/DesignerProperties.cs`

**`Microsoft.Phone.*` types are NOT here** — they live in the separate
`WPR.Framework.Phone` project, which is a real facade: it builds the assembly
`Microsoft.Phone` and declares the genuine `Microsoft.Phone.*` namespaces, so games
bind it without a patcher redirect at all. Its tree mirrors the namespace *below*
`Microsoft.Phone`:
- `Microsoft.Phone.Shell.PhoneApplicationService` → `Shell/PhoneApplicationService.cs`
- `Microsoft.Phone.Tasks.MediaPlayerLauncher` → `Tasks/MediaPlayerLauncher.cs`

Deciding which of the two a new WP type goes in is a recurring call — see the
`microsoft-phone-facade-vs-patcher` memory.

When adding a new shim type, look up the real upstream namespace (usually in
the type's MSDN docs or a Silverlight 4 reference assembly), create the
mirror directory if it doesn't exist, and drop the class file in it. The
filename is the type name verbatim. Keep the doc comment that says
`/// Shim for <c>System.X.Y.TypeName</c>.` — it's the canonical record of
which upstream type the file shadows, and tooling can grep for it.

Files at the project root are **not** type shims — they're WPR-internal
runtime/helper code that doesn't shadow any upstream type:
- The CPU renderer (`SoftwareVisualRasteriser*.cs`, `TextRasteriser.cs`, `TrueTypeFont.cs`,
  `WpChrome.cs`, `UIElementRenderer.cs`, `SilverlightImageDecoder.cs`). It is the only one: the
  **D3D11** renderers were retired on 2026-10-12 (the runtime convergence plan 2a) and the
  Avalonia ones (`SilverlightRenderer`, `BrandedSplashRenderer`, `IBackgroundRenderer`) on
  2026-10-13 (2b, Stage D). `DrawingSurfaceBackgroundGrid` is a plain Grid now.
  **Do not add a graphics or UI package reference to `WPR.Framework.Silverlight`** —
  `BackendIsolationTests` fails on a graphics one, and the project has no Avalonia reference
  since Stage D; keep it that way.
- Pointer-to-gesture bridge (`Gestures.cs`, `PanoramaState.cs`,
  `PanoramaStateTable.cs`, `PanoramaSelectedItemSync.cs`)
- XAML helpers (`XamlTypeConverter.cs`, `MarkupExtensionParser.cs`)
- Hosting glue (`HostContext.cs`, `HitTester.cs`, `BingWallpaper.cs`,
  `ResourceBundleReader.cs`, `GameMakerAssetExtractor.cs`)
- Theme constants (`PhoneTheme.cs`)
- `AssemblyInfo.cs`

If you're adding something that *is* a shim, it goes in the namespace tree.
If you're adding new hosting logic, it stays at the root.

`WPR.WindowsCompability` is still flat. **`WPR.XnaCompabilityPatch` no longer exists** — it was
deleted on 2026-08-29 once its last three types found better homes, so there is no
`WPR.XnaCompability` assembly any more:
- the WP7 `GraphicsDeviceManager` override → `Src/Backends/WPR.Backend.FNA/Compat/` (it subclasses
  FNA's spine manager, so the backend is its only legal home);
- the `GraphicsDevice` / `GraphicsAdapter` display-mode overrides → `WPR.Framework.Xna/Compat/`,
  namespace `WPR.Xna.Compat` (they only ever subclassed WPR-owned types, and `WPR.Loader` needs
  `typeof(...)` on them for `MemberPatches` — it references `WPR.Framework.Xna` directly for that).

Games no longer bind a `WPR.XnaCompability` identity at all, which is why the deletion was
reinstall-forcing (`ApplicationPatcher.Version` 16).

The mirror-tree convention has only been applied to `WPR.Framework.Silverlight` so
far. Apply the same pattern when you next touch those projects, but don't make a
separate pass just to reorganise them.

### CLI build shortcuts that work

Since `Src/Directory.Build.targets` gates the android legs, a full solution build no
longer trips over them when the workload is absent — but `Src/WPR.Windows.slnf` is
still the right entry point for desktop-only work (the android-only projects can't be
TFM-stripped). Historically this section warned about `NU1202` on `Avalonia.Android`
from a workload-version mismatch; gating handles that case now.

When verifying a small edit:

```
dotnet build <project>.csproj -c Debug -f net10.0-windows10.0.17763.0 \
    -maxcpucount:1 -nodeReuse:false --nologo -p:SolutionDir=<repo>/Src/
```

- `-f net10.0-windows10.0.17763.0` pins the desktop leg explicitly.
- `-p:SolutionDir=<repo>/Src/` with **forward slashes and a trailing slash** — many
  csprojs resolve `ProjectReference`s through it, and
  `Src/Backends/FNA.Platform/Directory.Build.props` shadows the one
  `Src/Directory.Build.props` sets. Omit it and FNA.Core cascades into CS0246 on every
  XNA type. (`build-desktop.ps1` does this for you.)
- `-maxcpucount:1 -nodeReuse:false` avoids the parallel-build CS0006
  "metadata file not found" race that hits in MSBuild's default settings.
- Build leaf projects first (e.g. `WPR.Framework.Silverlight`) — they have
  no project deps that need staging and give the fastest yes/no on a shim edit.
- Building the full chain up to `WPR.Platform.Windows` from the CLI **does** work, as long
  as `SolutionDir` is passed (verified 2026-08-21: 0 errors, ~7 s incremental). The
  older note here said it fails with spurious "namespace not found" cascades — that was
  the missing `SolutionDir`, not a CLI restaging limitation. `build-desktop.ps1` is the
  convenient wrapper.

### Verifying a patcher entry took effect

If the user says "still the same error after reinstall," check whether
`ApplicationPatcher.PatchDll` actually wrote a `.dll.original` sibling next to
the user assembly in the per-game install dir (its path is built in
`ApplicationInstaller.CreateApplicationEntryAndExtract`; ask the user for the
exact folder once and stash it for the session). If the `.original` is older
than the patcher source changes (or missing entirely), the install didn't
re-run — the user may have hit "launch" instead of "reinstall," or the install
dir wasn't cleared.

### Files duplicated between the two platform heads

There is no shared UI project any more. `Src/UI/WPR.UI` was dissolved on
2026-08-29: the Avalonia UI (`Pages/`, `ViewModels/`, `Views/`, `Themes/`,
`ViewLocator`), the three launchers (`SilverlightLauncher` — deleted 2026-10-13 with the Avalonia
Silverlight host — `XnaLauncher`, `UnityPortLauncher`), the tilt stack (`KeyboardTiltBindings`, `TiltOverlay`,
`KeyboardAccelerometerHost` — since 2026-08-30 under `Input/`, namespace
`WPR.Platform.Windows.Input`) and `PhoneHardwareButtons` /
`WP7AccentColors` all went to `WPR.Platform.Windows`, because the Android shell is
native and used none of them. `PixelToGridLengthConverter`, `ProgressView`,
`RegistrationPage`, `RegistrationService` and `System.Windows.MessageBoxButton`
were deleted — nothing referenced them.

### `Src/Core/WPR.Shell` — the launcher shell minus its UI (2026-09-02)

Six files used to be copied into both heads with "change one, change the other" as the only
enforcement. Most of that is now one shared project.

**The invariant: nothing in `WPR.Shell` may name a UI type.** No Avalonia `Control`/`Window`/
`IBrush`, no Android `Activity`/`Context`/`View`. That is what lets an Avalonia head and a native
Android head share it. Verify it structurally rather than by eye — the built assembly must
reference no UI framework, and its android leg must carry zero `Android.*` typerefs:

```powershell
$m=[Mono.Cecil.AssemblyDefinition]::ReadAssembly("...\WPR.Shell.dll")
$m.MainModule.AssemblyReferences | % { $_.Name }        # expect WPR.Common, WPR.Database, WPR.Framework.Xna
$m.MainModule.GetTypeReferences() | ? { $_.Namespace -match '^(Android|Java)' }   # expect nothing
```

What moved in, and why each was worth it:

| in `WPR.Shell` | was |
| --- | --- |
| `Resources.resx` + `Resources.Designer.cs` | **byte-identical** in both heads — 65 launcher strings. Reach them as `WPR.Shell.Resources`; qualify it, because a bare `Resources` binds to Avalonia's `StyledElement.Resources` and to Android's `Activity.Resources` and both shadow it silently. |
| `LocaleUtils.cs` | identical. Generic over `Enum`, so it needs no reference to `WPR.Loader` for `ApplicationInstallError`. |
| `ApplicationLaunchRequest.cs` | differed by one line; the Android copy's `Log.Error` was strictly better and is what survived. |
| `AchievementRollup` + `AchievementTotals` | the four aggregates existed in **four** places, the sort comparator in **three**, the description fallback in two. |
| `WP7AccentPalette` | the same twenty name/hex pairs in both heads, under a comment asking the reader to edit both by hand. |

**The achievement extraction fixed three real divergences the duplication was hiding**, which is
the argument for doing this kind of extraction at all rather than just tidying:

- Only the Android shell masked **secret** achievements, so the desktop achievements page revealed
  the name and description of unearned secret achievements — the one thing the flag exists to
  prevent. `AchievementRollup.DisplayName` / `DescribeUnearnedSafely` are now the only way either
  shell renders achievement text.
- The two shells disagreed on completion percentage (`double` vs rounded `int`) and on game order.
- One desktop copy fed null product ids to `ToDictionary` and degraded the whole page to an empty
  list via a catch. `ByProduct` filters them and compares `OrdinalIgnoreCase`.

**The accent palette shows the shape to copy when a head "can't" share something.** The blocker was
that the desktop type eagerly built an `Avalonia.Media.IBrush` per entry. The fix was to keep the
brush *out of the shared data*, not to keep two copies of the data: `WP7AccentPalette` holds the
pairs, and each head projects them into its own paint type. Prefer that over a second copy.

**TFM list mirrors `WPR.Database` and must keep doing so** — on Windows that project has no plain
`net8.0` leg, so a bare `net8.0` here fails restore with NU1201.

**Two files are still duplicated, deliberately** (a third, `System/Windows/MessageBox.cs`, was an
`internal` placeholder and was deleted on 2026-10-16; see the `MessageBox.Show` section):

| file | why it stays |
| --- | --- |
| `MessageBoxUtils.cs` | genuinely different implementations — Avalonia windows vs `AlertDialog`. Only the contract is common: two delegates, and since 2026-10-16 `ShowGameMessageBoxAsync` behind Silverlight's `MessageBox.Presenter`. |
| `ServicesSetup.cs` | no longer where platform differences live: since 2026-09-01 each head declares a `PlatformDescriptor` (`WindowsPlatform` / `AndroidPlatform`) and `Start()` is one `PlatformComposition.Apply` call plus the `Guide`/`MessageBox` wiring. **Compare the two descriptors, not these files.** |

**Still duplicated and not yet extracted** (measured 2026-09-02, in rough value order): the install
pipeline shape — `ReadPreview` → `Install(stream, progress, confirmReplace, token)` → the
`err != None && err != Canceled` error-mapping predicate — exists in **three** places
(`ApplicationListingPage`, `BatchReinstall`, `XapInstallFlow`) with three different answers to the
confirm-replace question, and Windows *discards* the `ApplicationInstallError` that repatch returns
while Android surfaces it. The bootstrap recipe (`Configuration.Current` → seed `applications.db` /
`achievements.db` → copy `Database/Achievements` → `ReconcileCatalogueGamesAsync`) is ~22 lines
duplicated between `Program.Main` and `WprStartup`, one line of it character-identical; only the
byte source differs (filesystem vs `AssetManager`), so it parameterises on a `copyIfMissing`
delegate. The install-folder expression
`Configuration.Current!.DataPath(Application.DataStoreFolder)` + ProductId appears **four** times.
None of these name a UI type in the part that is common.

Namespaces follow the head: `WPR.UI` → `WPR.Platform.Windows` /
`WPR.Platform.Android`. Note that inside `namespace WPR.Platform.Android`, the
identifier `Android` binds to *that* namespace, not the Mono.Android root — so
Android-copy code writes `global::Android.Resource.String.Ok`. The rest of the
existing Android files already do this; match them.

Avalonia `avares://` URIs use the **assembly** name, not the project name:
`avares://WPR.Platform.Windows/Themes/Brand.axaml`.

### Achievement notifications (2026-08-31)

`SignedInGamer.BeginAwardAchievement` posts the unlock toast through
`WPR.Common.NativeUI.NotificationManager`. Until 2026-08-31 **nothing in the Android head ever
assigned that slot**, so every unlock NullReferenced into that method's own `catch`, logged
`Fail to display Achievement notification`, and showed the player nothing — while still awarding
and persisting the achievement, which is why it went unnoticed. `AndroidNotificationManager` had
existed the whole time and was never constructed.

Three parts, all in the Android head:

- `ServicesSetup.Start()` assigns the manager, built over
  `global::Android.App.Application.Context` — **not** the activity. `Start()` runs again in
  `GameActivity`'s `:game` process, and holding the activity there would pin it for the whole run.
- The manifest declares `POST_NOTIFICATIONS`, and `MainActivity` requests it on API 33+.
  Requested in the **launcher**, never in `GameActivity`: permissions are per-app, not
  per-process, so the launcher's grant covers `:game` too — and a system permission dialog over a
  game that is mid-launch would be worse than no notification. The answer is deliberately not
  acted on; declining just means no unlock toasts.
- `AndroidNotificationManager.LaunchActionId` returned `throw new NotImplementedException()`. It
  is part of the `INotificationManager` surface, so a host that merely probed it would have taken
  the process down; it returns null now.

The notification's `SoundUri = "AchievementUnlocked"` resolves to
`android.resource://com.wpr.android/raw/achievementunlocked` — `Resources/raw/AchievementUnlocked.mp3`
already shipped, and aapt lowercases the name, which is what the `.ToLower()` in
`ShowNotification` matches. `ImagePath` goes through `Configuration.DataPath(_IconPath)`, and
`IconRelativePath` is built with forward slashes, so it resolves on Android exactly as it does on
the achievements list screen.

### Only a NEWLY earned achievement toasts (2026-09-18)

`SignedInGamer.BeginAwardAchievement` flipped rows inside a loop that skipped anything already
`IsEarned` — and then showed the notification **outside** that loop, guarded only by
`achievements.Count != 0`. So a re-award of an achievement the player already held still toasted.
The symptom is every unlock from an earlier session replaying on the next launch of the game.

**Re-awarding is normal, not a game bug**, which is why this is fixed here rather than per title:
WP7 ports routinely re-assert their whole unlocked set when they load a save, and progression
checks call `AwardAchievement` on every evaluation rather than only on the transition. Nothing
about it is platform-specific — the DB row is correctly persisted and correctly skipped; only the
toast escaped the check.

The loop now captures the first row it actually flips (`newlyEarned`) and the notification hangs
off that. Two details worth keeping:

- **The toast must describe the row that was earned, not `achievements[0]`.** When a key matches
  more than one row — the data defect the `achievements.Count > 1` warning above it reports — the
  first row can be the already-earned one, so the old code could name the wrong achievement.
- **`SuppressFocusActivation` moved inside the guard.** It exists only to absorb the focus blip the
  desktop toast causes, so asking for an 8-second window when no toast is coming would suppress a
  genuine `OnActivated`/`OnDeactivated` the game was entitled to see.

`[wpr-achievement] '<key>' already earned … re-award ignored` goes to the per-game
`wpr_game_debug.log` on the suppressed path — via `Trace`, because `WPR.Common.Log` writes to
stdout and a `WinExe` discards it. Without that line a suppressed re-award and a notification
manager that silently failed look identical from inside a game.

No `ApplicationPatcher.Version` bump and no reinstall — this is shim behaviour in
`WPR.Framework.Xna`, so games pick it up on next launch.

## Cleanup at end of session

Before finishing a task, clean up anything created for diagnosis/verification
that isn't part of the change itself:

- **Log files**: anything I wrote into the repo root or under `Src/` for build
  capture (e.g. `build_*.log`, `restore_*.log`, `install_*.log`). Leave
  pre-existing files alone — only remove ones I authored this session.
- **Stray scratch csprojs**: anything I created purely to probe SDK behavior
  should be removed before declaring done. (The repo-root `global.json`
  pinning to the 8.0 band is **not** scratch — it's part of the committed build
  config; leave it.)
- **Build processes**: check for orphaned `dotnet` / `MSBuild` /
  `VBCSCompiler` instances I spawned (`Get-Process` + match command line).
  Do **not** kill processes belonging to Rider (`ReSharperHost`,
  `JetBrains.*`) or Visual Studio (`devenv`) — those are the user's IDE.
- **`obj/` and `bin/`**: leave these. They're normal incremental-build
  artifacts; removing them would force a rebuild the user didn't ask for.

### Android TFM gating (`Src/Directory.Build.targets`)

19 projects target `net8.0-android`, 13 of them multi-targeting (the `WPR.UI`
project that used to make 18 was dissolved into the platform heads).
`Src/Directory.Build.targets` detects whether the .NET Android workload is
installed **for the SDK band this repo builds with**, and strips `*-android` from
`$(TargetFrameworks)` repo-wide when it isn't — otherwise a clone with a plain .NET 8 SDK can't build the *desktop*
app either (NETSDK1147 on the android leg kills the whole build).

Detection is `Exists(<dotnet-root>/packs/Microsoft.Android.Ref.$(WprAndroidApiLevel))`,
where `WprAndroidApiLevel` is 34 (see the API mapping below).

Two traps are baked into that file. Both cost a long debugging session on
2026-08-21; read the comments there before editing it.

1. **Property functions escape their return value.** `Regex::Replace` hands back
   `net8.0%3bnet8.0-windows10.0.17763.0` — one value with an escaped semicolon,
   no longer a list. `$(TargetFrameworks)` still prints correctly in a `Message`
   or a `Condition`, so the property looks fine, but
   `_ComputeTargetFrameworkItems` (SDK's `Microsoft.Common.CrossTargeting.targets`)
   can't split it and launches every inner build with `TargetFramework` set to
   the whole string. The moniker comes back `Unsupported,Version=v0.0`,
   nearest-TFM matching fails, and every `ProjectReference` to a multi-targeting
   project dies with

   ```
   error : Project '...\WPR.Framework.Phone.csproj' targets
   'net8.0;net8.0-windows10.0.17763.0'. It cannot be referenced by a project
   that targets '.NETCoreApp,Version=v8.0'.
   ```

   — which names two *compatible* frameworks, so it reads as nonsense and sends
   you hunting a TFM mismatch that doesn't exist. The fix is
   `$([MSBuild]::Unescape(...))` around the result. Verify any change with:

   ```
   dotnet msbuild <proj> -getTargetResult:GetTargetFrameworks
   ```

   `TargetFrameworkMonikers` must read `.NETCoreApp,Version=v8.0` once per
   remaining TFM.

2. **Don't widen detection to a `Microsoft.Android.Sdk.*` glob.**
   `dotnet workload install android` installs into whichever SDK band resolves
   at the time, and bands don't share packs. Install it under .NET 10 — e.g. by
   running the command from a directory where this repo's `global.json` doesn't
   apply — and `packs/` fills with `Microsoft.Android.Sdk.Windows/36.x` +
   `Microsoft.Android.Ref.36` while the 8.0 band still has no android workload
   (`dotnet workload list` under 8.0.4xx prints nothing). A glob reports
   "installed", the android legs get built, and all 14 multi-targeting projects
   fail NETSDK1147 from an SDK whose `packs/` is visibly full of android.

Consequences to remember:
- **On this machine the workload IS now installed for the 8.0 band** (since late
  2026-08-21), so gating resolves `true` and a *desktop* build also compiles the
  `net8.0-android` leg of all 14 multi-targeting dependencies. Correct, but a cold
  desktop build now pays for the android compile too (measured: android head alone
  ~60 s; incremental desktop once those outputs exist, 7.7 s — the cold combined
  figure has not been measured). Pass `-p:IncludeAndroidTargets=false` for a
  desktop-only loop.
- When the workload IS detected the announce line does **not** print: that Message
  is `Importance="normal"`, below default verbosity. So absence of the `WPR:` line
  now means either "detected" or "a dependency failed first" — check
  `packs\Microsoft.Android.Ref.34` to tell them apart.
- If Android stops being built here, check for the *ref* pack:
  `dir "$env:ProgramFiles\dotnet\packs\Microsoft.Android.Ref.34"`. Force with
  `-p:IncludeAndroidTargets=true` (that only restores the TFM — it does not
  conjure the packs, so the leg will then fail NETSDK1147 for real).
- The gating never empties a project's `TargetFrameworks`: android-only projects
  spell their TFM as singular `<TargetFramework>` and aren't touched.
- `build-android.ps1` / `build-android.sh` / `release.yml` all pass
  `-p:IncludeAndroidTargets=true`, so CI never depends on detection.
- Desktop-only contributors should open `Src/WPR.Windows.slnf`, not `WPR.sln` —
  the filter drops `WPR.Platform.Android`, `WPR.Audio.AndroidMediaPlayer`, and the Java
  bindings, which are android-only and can't be TFM-stripped.
- The announce line (`WPR: .NET Android workload for API 34 ...`) only fires on
  `WPR.Platform.Windows`'s `Build`, so it does **not** print when a dependency's
  android inner build fails first — don't read its absence as "gating didn't run".

## Building the Android leg (WPR.Platform.Android)

> **Status 2026-08-21 (late): the Android leg builds and runs again.** Verified
> end to end: `dotnet build` 0 errors (~60 s), APK installed on the `Pixel_Dev`
> emulator (API 36 x86_64 — an API 34 APK runs fine on a newer device), app
> launched, UI rendered, no exceptions in logcat.
>
> What it took, after the 2026-08-20 toolchain wipe (see history note):
>
> ```powershell
> # 1. android workload into the 8.0 band. There is NO --sdk-version flag; the band
> #    follows whichever SDK resolves, so cwd + global.json IS the mechanism.
> Set-Location C:\Users\BenSl\RiderProjects\WPR   # so global.json applies
> & "C:\Program Files\dotnet\dotnet.exe" --version          # must print 8.0.4xx
> & "C:\Program Files\dotnet\dotnet.exe" workload install android
>
> # 2. Android SDK bits. Use the `android` CLI - `sdkmanager` is deprecated and
> #    warns on every run. Note the separator changed from `;` to `/`.
> & "C:\Android\Sdk\cmdline-tools\latest\bin\android.exe" sdk install "platforms/android-34" "build-tools/34.0.0"
> ```
>
> Confirm with `dotnet workload list` (expect `android  34.0.154/8.0.100`) and
> `dir "$env:ProgramFiles\dotnet\packs\Microsoft.Android.Ref.34"` — the ref pack is
> exactly what the TFM gating tests.
>
> `android.exe` self-downloads its payload on first run and prints the SDK licence
> terms, so the first invocation is interactive.
>
> Running it on the emulator:
>
> ```powershell
> Start-Process "C:\Android\Sdk\emulator\emulator.exe" -ArgumentList "-avd","Pixel_Dev"
> C:\Android\Sdk\platform-tools\adb.exe wait-for-device
> C:\Android\Sdk\platform-tools\adb.exe install -r -t "Src\Platforms\WPR.Platform.Android\bin\Debug\net8.0-android34.0\com.wpr.android-Signed.apk"
> C:\Android\Sdk\platform-tools\adb.exe shell monkey -p com.wpr.android -c android.intent.category.LAUNCHER 1
> C:\Android\Sdk\platform-tools\adb.exe logcat -d | Select-String "WPR|FATAL"
> ```
>
> A healthy start logs `WPR: MainActivity OnCreate completed (native shell)`. The
> launcher activity is `com.wpr.android/.MainActivity` (the manifest `package` is
> `com.wpr.android`, which does **not** match `$(ApplicationId)` =
> `com.MediaExplorer.WPR` in the csproj — pre-existing inconsistency, the manifest
> wins).
>
> Older notes here looked for `WPR: ApplicationLifetime = Avalonia.Android.SingleViewLifetime`
> and `AVALONIA: Surface Created`. Neither line exists any more — see "The Android
> shell is native" below.

### The Android shell is native (no Avalonia)

The launcher UI on Android — Start, games, achievements, settings, about — is
plain `android.app.Activity` with XML layouts, not Avalonia. `MainActivity` is a
Windows Phone style tile Start screen; the rest live in
`Src/Platforms/WPR.Platform.Android/Native/`. `GameActivity` is untouched: it
still hosts one game run under SDL in its own `:game` process.

Consequences worth knowing before editing:

- **The Avalonia pages live in the Windows head.** `ApplicationListingPage`,
  `SettingsPage`, `MainViewNavigator` etc. are under
  `Src/Platforms/WPR.Platform.Windows/` and affect the Windows head alone. The
  Android equivalents are the `Native/*Activity.cs` files and must be changed in
  parallel when behaviour should match.
- **`Avalonia.Android` is still referenced and must stay.** Nothing initialises
  Avalonia in the launcher process, but that package supplies the AndroidX
  AppCompat resources `MyTheme.NoActionBar` parents onto — and that style is what
  `GameActivity` and the splash use.
- **No library scan on Android.** `WPR.LibraryScanner` is never constructed in
  this head; games are added one at a time through the system document picker
  (`Native/XapInstallFlow.cs`). Don't "restore" folder scanning here — scoped
  storage makes it a permissions fight for a worse result.
- **`WprStartup.EnsureInitialized`** replaces the old
  `MainActivity.SetupConfigurationAndDatabase` / `SetupDllPatchForCecil` pair. It
  is idempotent and every launcher activity calls it, because Android can
  recreate the process directly into any of them.

`Src/Platforms/WPR.Platform.Android/README.md` has the fuller tour.

The setup the Android build needs:

- **`global.json`** at the repo root pins the SDK to the **8.0 feature band** (`version: 8.0.100`, `rollForward: latestFeature`, so any installed 8.0.1xx+ SDK satisfies it).
  Without this, MSBuild picks the .NET 10 SDK and loads only the .NET 10 Android workload
  manifest, which doesn't ship `net8.0-android*` ref packs → `Mono.Android.dll` doesn't
  resolve and you get CS0234 on `Android.Content` / `Android.Graphics` / `AssetManager`.
  The pin also decides which band `dotnet workload install` targets, so **always run
  workload commands from the repo root**.
- **.NET 8 SDK** is installed system-wide at `C:\Program Files\dotnet` alongside .NET 10.
  The android manifest ships with the SDK at
  `C:\Program Files\dotnet\sdk-manifests\8.0.100\microsoft.net.sdk.android\` — its
  presence says nothing about whether the *workload* is installed. Check `packs/` for that.
- **Android SDK** lives at `C:\Android\Sdk`, with `ANDROID_HOME` / `ANDROID_SDK_ROOT`
  set as **machine-level** env vars (the user-level ones are empty). Needs
  `platforms\android-34`.
- **JDK**: `JAVA_HOME` = `C:\Program Files\Microsoft\jdk-21.0.12.8-hotspot`
  (Microsoft OpenJDK 21). Android Studio is no longer installed, so the old
  `...\Android Studio\jbr` path is gone.

### Rider
With `global.json` committed and the machine env vars set, Rider needs no extra config.
Verify with `& "C:\Program Files\dotnet\dotnet.exe" --version` from the repo root — it
must print `8.0.4xx`. If it prints `10.0.x`, the `global.json` isn't being picked up.

### CLI build recipe (still useful for headless verification)

```powershell
$env:ANDROID_HOME      = "C:\Android\Sdk"
$env:ANDROID_SDK_ROOT  = $env:ANDROID_HOME
$env:JAVA_HOME         = "C:\Program Files\Microsoft\jdk-21.0.12.8-hotspot"
& "C:\Program Files\dotnet\dotnet.exe" build `
    "Src\Platforms\WPR.Platform.Android\WPR.Platform.Android.csproj" `
    -c Debug -maxcpucount:1 -nodeReuse:false --nologo `
    -p:AndroidSdkDirectory="$env:ANDROID_HOME"
```

Output: `Src\Platforms\WPR.Platform.Android\bin\Debug\net8.0-android34.0\com.wpr.android-Signed.apk` (~200 MB in Debug: EmbedAssembliesIntoApk + AndroidEnableAssemblyCompression=false + the bundled achievement catalogues. The old "~31 MB" figure predated those.)

### .NET / Android API mapping (Microsoft locked these)
- `net8.0-android*` → API **34** only. There is no `net8.0-android35.0`.
- `net9.0-android*` → API **35** only.
- `net10.0-android*` → API **36** only.

Also: `Avalonia.Android` skipped .NET 9. Version 11.x ships only `lib/net8.0-android34.0/`;
12.x ships only `lib/net10.0-android36.0/`. To move off API 34 you must move all the way
to net10 + Avalonia 12.

### What changed (history note — 2026-08-21)
Something rewrote `C:\Program Files\dotnet` on 2026-08-20 (all folders stamped
21:57), leaving **only** .NET 10.0.400 + runtime 10.0.11: the .NET 8 SDK, the .NET 8
runtime and the android workload were all gone. `global.json`'s 8.0 pin then couldn't
resolve at all ("A compatible .NET SDK was not found"), so nothing built and Rider
couldn't run `WPR.UI.Desktop`. Reinstalling the .NET 8 SDK (now **8.0.424**) fixed that.

The same event replaced the Android toolchain: Android Studio and both previously
documented Android SDK locations
(`C:\Users\BenSl\AppData\Local\Android\Sdk`, `C:\Program Files (x86)\Android\android-sdk`)
no longer exist; the SDK is now `C:\Android\Sdk` with only android-36, and the JDK is
Microsoft OpenJDK 21.

It also exposed two latent bugs in `Src/Directory.Build.targets`, whose strip branch had
never once executed on this machine (the workload had always been present). Both are
fixed and documented under "Android TFM gating" above.

### What changed (history note — 2026-05-25)
Earlier CLAUDE.md said .NET 8 SDK was only present at user-local
`C:\Users\BenSl\.dotnet`, that Rider couldn't see it, and that `global.json` pinning
would fail with NETSDK1141. That was true when written; .NET 8 SDK + android workload
were then installed system-wide, which is the arrangement the notes above assume.

## Environment notes (as of 2026-08-21)

- System .NET SDKs: `C:\Program Files\dotnet\sdk\` — **8.0.424** and **10.0.400**
  side-by-side. `global.json` at the repo root pins the build to the 8.0 band.
  Shared runtimes: `Microsoft.NETCore.App` **8.0.30** and **10.0.11**.
- **The `android` workload IS installed for the 8.0 band** — `dotnet workload list`
  from the repo root prints `android  34.0.154/8.0.100  SDK 8.0.400`. (It was absent
  earlier on 2026-08-21; installed late that day.) `sdk-manifests\` has `8.0.100`,
  `10.0.100` and `10.0.400`, but manifests ship with the SDK and prove nothing about
  installation — check `packs\` instead.
- Android packs in `C:\Program Files\dotnet\packs\`: `Microsoft.Android.Ref.34`
  (the one this repo needs, and exactly what TFM gating tests) and
  `Microsoft.Android.Ref.36`; `Microsoft.Android.Sdk.Windows` 33.0.95 / 34.0.154 /
  35.0.105 / 36.1.69. The 35/36 entries are .NET 10 band leftovers from a
  workload install that ran outside the repo root — harmless but inert here, and
  the reason gating must test for `Ref.34` specifically rather than globbing
  `Microsoft.Android.Sdk.*`.
- User-local .NET at `C:\Users\BenSl\.dotnet`: **no SDK at all**, only first-use
  sentinel files. Ignore it; don't try to build against it.
- Android SDK: `C:\Android\Sdk` — `platforms\android-34` + `android-36`, and
  `build-tools\34.0.0` + `37.0.0`. `ANDROID_HOME` and `ANDROID_SDK_ROOT` are set machine-wide
  to this path; the user-level vars are empty.
- JDK: `C:\Program Files\Microsoft\jdk-21.0.12.8-hotspot` (`JAVA_HOME`). Android Studio
  is not installed.
- Once the android workload is installed for the 8.0 band, `net8.0-android` and
  `net8.0-android34.0` both resolve to its API 34 ref pack.
  `SupportedOSPlatformVersion` can range from `21.0` up to `34.0` (must be ≤ TPV;
  NETSDK1135 fires if higher).
- Desktop build is verified working at this state: `WPR.Platform.Windows`, `Debug`,
  `net8.0-windows10.0.17763.0`, 0 errors (189 pre-existing warnings), ~7 s incremental.
