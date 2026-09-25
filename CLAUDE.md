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

**Avalonia is still split — 11.3.9 on desktop, 12.1.3 on android — and that is NOT because
the TFM blocked it.** net10 removes the *restore* obstacle, and the desktop could take 12.1.3
tomorrow; what stops it is four migrations riding along behind the version number, chief among
them that `Avalonia.ReactiveUI` stops at 11.3.9 and its successor pulls **ReactiveUI 24**, where
`System.Reactive.Unit` became `RxVoid`. The full measured list is on the `AvaloniaVersion`
property in `WPR.Framework.Silverlight.csproj` — **read it before starting, because "bump the
version" is the wrong mental model.** A trial run on 2026-09-25 reached 84 compile errors with
more behind them, and Avalonia 12's XAML and theme changes do not surface at compile time at
all, so this needs a real pass through the UI afterwards.

### How runs actually happen

- **Build**: the user clicks build/run in Rider, which builds `WPR.Platform.Windows`
  for `net10.0-windows10.0.17763.0` and pulls in the rest by project reference.
- **Run**: `WPR.Platform.Windows` is the entry point (assembly/exe `WPR.Platform.Windows`, renamed from `WPR.UI.Desktop` on 2026-08-29). The UI lists installed games;
  picking one launches via `SilverlightLauncher.LaunchAsync` or
  `XnaLauncher.LaunchAsync`.
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
`IAccelerometerProvider` and its own slot on `SensorBackend`, when its WP7 shim is actually
written — never more members on this one, and never a new project.
`WPR.Framework.Devices.Sensors` ships only `Accelerometer` today, so there is nothing else to
model yet.

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

**Not built:** none of this exists for Silverlight titles. They run a separate Avalonia pointer
pipeline that never touches `TouchPanel`, so keyboard→touch is XNA-only and would need a second
implementation there.

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

Also: `WPR.Framework.Silverlight`'s pointer pipeline (`Gestures.cs` + `PhoneApplicationFrameView`)
is a *separate* recognizer over Avalonia pointer events that never touches `TouchPanel`. Two
systems by design, not by accident — don't "unify gestures".

**The hardware Back press is on `IGameHost`**: `PressBackButton()`, implemented by `FnaGameHost`
over `WprPhoneBackButton`. Before this, `GameActivity` held a `FnaGameHost` typed field purely to
reach `PressPhoneBackButton()`. **Back only** — WP7's Start and Search deactivate the app rather
than reaching the game, which is why the Silverlight bezel wires them as no-ops and why the other
two would be members no host could implement. A future "Start button means Back" input binding
still just calls this.

**The XNA and Silverlight Back paths stay separate**, deliberately: one is a level-sampled gamepad
button held for a frame, the other is a routed event a page can cancel before `GoBack()`.
Silverlight apps aren't driven by `IGameHost` at all and there is no Silverlight path on Android,
so a unifying interface would have two implementations and no polymorphic caller.

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
| mark attempting | `FnaGameHost.RunAsync`, immediately after `GraphicsDriverSelection.Apply` |
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

**What it cannot see**, and this is not a gap to be closed by tuning it: a driver that initialises,
presents, and dies a minute later is marked working at frame one and never demoted. **3D Brick
Breaker Revolution is exactly that case** — see the descriptor-pool section below. The Settings
picker remains the answer there; the two mechanisms are complementary and neither replaces the other.

### Capabilities, not driver names (2026-09-18)

`IGraphicsCapabilities` + `GraphicsCapabilities` + `GraphicsCapabilitiesStore`
(`Src/Engine/WPR.Engine.Graphics/`). Measured in `FnaGraphicsBackend.PublishCapabilities` at the
first presented frame — the earliest point where a device exists *and* has demonstrably worked —
and surfaced as the `graphics` section of `GameInfoActivity`.

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
- **The OpenGL driver hangs this game even earlier**, on frame 2 — the `ForceToMainThread` queue from
  the graphics section above. So the Settings picker is no escape hatch for PvZ, and the probe
  cannot help either: the game presents plenty of frames before it stops.
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
  is what the driver sees. This is the opposite of `Texture2DReader`'s DXT-to-`Color` substitution,
  deliberately: that reader owns the whole lifecycle of a content texture, while a game-built one
  has the game sizing its own arrays from `Format`. Reporting the substitute is exactly what the
  TEMP block did, and exactly why it dropped every upload.
- **Round-trip is bit-exact** — 4-bit channels expand by `n*17` (`n<<4|n`, so `>>4` recovers `n`),
  5-bit by `x<<3|x>>2`, 1-bit alpha to 0/255 — because a game that reads a texture, edits it and
  writes it back must get its own bits.
- **Both write paths are hooked**, `SetData<T>` *and* `SetDataPointerEXT`. The latter is what
  `Texture2D.FromStream` and `TextureCube.DDSFromStreamEXT` use; missing it would leave every
  stream-loaded texture undefined.

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
serializer — so `--repatch-installed` is enough. **The current version is 38**; see the next
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
- **`assembly-store-reader` is no longer referenced** by the Android head. The project is still in
  the tree and the solution; delete it once it is clear nothing wants it back.

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

**Not everything is a capability.** `SilverlightBackend.SurfaceRenderer` stays a direct
registration in the Windows head: it is a framework-internal renderer choice, not a fact about the
device. The per-launch RHI seams are filled by `FnaGameHost`, not from here, because their lifetime
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
repatched or reinstalled** (the current version is 38) —
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

**This was a patcher table change (v20).** The current version is **38** — see "Windows path
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
| `WPR.Engine.Audio` | `WPR.Audio.FAudio`, `WPR.Audio.AndroidMediaPlayer` |
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

- **CPU, not Avalonia.** `SilverlightRenderer` beside it draws into an Avalonia `DrawingContext`,
  and the mixed-mode host never initialises Avalonia — that is what lets these titles run on
  Android at all. So this is a second renderer over the same tree, on purpose.
- **Output is premultiplied RGBA packed as XNA's `Color`** (R in the low byte). Games blit it with
  a default `SpriteBatch.Begin()`, which is `BlendState.AlphaBlend`, i.e. premultiplied; a
  straight-alpha buffer fringes every edge.
- **It paints only what the tree asks for**, with no page backdrop — unlike
  `SilverlightRenderer.RenderPage`, which fills black first. An unrequested fill here would hide
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
class does not resolve at JIT time. `SilverlightRenderer.DrawProgressBar` now matches our shim's
own FullName as well as the two WP7 ones — matching only the WP7 name would have stopped
recognising it the moment the type existed.

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

`MixedModeGame.Draw` composites the Silverlight page only when **nothing else painted the frame**.
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
- Renderers (`SilverlightRenderer.cs`, `BrandedSplashRenderer.cs`, …). Note the **D3D11**
  renderers are no longer here: Stage 5e (2026-08-29) moved `D3D11SurfaceRenderer`,
  `D3D11ImageSplashRenderer` and `D3D11TestPatternRenderer` into
  `Src/Backends/WPR.Backend.Direct3D11`, leaving the `ISurfaceRendererBackend` seam +
  `SilverlightBackend` registry behind. **Do not add a graphics package reference to
  `WPR.Framework.Silverlight`** — `BackendIsolationTests` fails if you do, and the fix is
  to put the code in the backend instead.
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
`ViewLocator`), the three launchers (`SilverlightLauncher`, `XnaLauncher`,
`UnityPortLauncher`), the tilt stack (`KeyboardTiltBindings`, `TiltOverlay`,
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

**Three files are still duplicated, deliberately:**

| file | why it stays |
| --- | --- |
| `MessageBoxUtils.cs` | genuinely different implementations — Avalonia windows vs `AlertDialog`. Only the contract is common, and it is two delegates. |
| `ServicesSetup.cs` | no longer where platform differences live: since 2026-09-01 each head declares a `PlatformDescriptor` (`WindowsPlatform` / `AndroidPlatform`) and `Start()` is one `PlatformComposition.Apply` call plus the `Guide`/`MessageBox` wiring. **Compare the two descriptors, not these files.** |
| `System/Windows/MessageBox.cs` | an `internal` placeholder holding `ShowSimpleImpl`, entangled with the head-specific dialog wiring above. |

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
  the filter drops `WPR.Platform.Android`, `WPR.Audio.AndroidMediaPlayer`, the Java bindings and
  `assembly-store-reader`, which are android-only and can't be TFM-stripped.
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
