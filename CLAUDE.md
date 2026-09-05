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

### How runs actually happen

- **Build**: the user clicks build/run in Rider, which builds `WPR.Platform.Windows`
  for `net8.0-windows10.0.17763.0` and pulls in the rest by project reference.
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
[wpr-platform] Android: accelerometer=EssentialsAccelerometerProvider vibration=AndroidVibratorProvider driver=OpenGL …
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
live in a backend. They are now `WPR.Backend.FNA/Input/`, attached by `FnaGameHost` when a head
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


### Android graphics: FNA3D picks a driver, and which one it picks matters

`FNA3D_PrepareWindowAttributes` walks `drivers[]` in order and takes the first whose
`PrepareWindowAttributes` succeeds. The compiled-in set differs per platform:

| head | drivers available | what actually gets picked |
| --- | --- | --- |
| Windows | D3D11, OpenGL | **D3D11** |
| Android | OpenGL, Vulkan | OpenGL is offered first and **declined**, so it fell through to **Vulkan** — until `fna3d.env` started forcing OpenGL |

Vulkan is the one FNA3D's own source still gates behind
`/* TODO: Bump this to the top when Vulkan is done! */` (`FNA3D.c:45`). This is the **only**
graphics-stack difference between the two heads, so it is the first thing to suspect for anything
that renders correctly on desktop and wrongly on Android.

**This is confirmed, not theorised** (2026-08-31). Mirror's Edge drew its world, lighting and
reflections perfectly on Android while every skinned character stayed in bind pose — a T-pose —
and forcing the OpenGL driver fixed it on real hardware. It animates via the stock
`SkinnedEffect`, whose vertex shader carries `float4x3 Bones[72]` at `_vs(c26)` indexed by a vertex
attribute (~242 uniform vec4s, relative-addressed): exactly what the unfinished Vulkan driver
mistranslates. Windows, on D3D11, animated it correctly all along.

**Expect this to have fixed more than one game.** Nothing about the failure is Mirror's Edge
specific — any XNA title using `SkinnedEffect` for 3D character animation was T-posing on Android
for the same reason. Worth re-testing the Android column of the compat list against this.

`Src/Platforms/WPR.Platform.Android/fna3d.env` now forces `FNA3D_FORCE_DRIVER=OpenGL` via
`@(AndroidEnvironment)`. Three things to know before touching it:

- **It must be a real process environment variable.** FNA3D reads the hint through `SDL_GetHint`,
  which falls back to `SDL_getenv`; .NET's `Environment.SetEnvironmentVariable` does **not**
  propagate to the native environ on Unix, so FNA's own `gldevice` launch-parameter path
  (`FNAPlatform.cs:54`) cannot set it on Android.
- **Forcing is a hard selection, not a preference.** The loop `continue`s past every driver whose
  `Name` doesn't `strcmp`-match, so if the forced driver declines you get "No supported FNA3D driver
  found!" and device creation fails — the game won't start at all rather than falling back. The name
  is exactly `"OpenGL"` (`FNA3D_Driver_OpenGL.c:6179`).
- **The emulator cannot render the OpenGL path**, so it does not validate it. On Pixel_Dev
  (API 36 x86_64) the GL driver initialises and reports `OpenGL ES 3.1`, then the screen stays on
  the game's own clear colour — for Mirror's Edge a flat white, which is why it reads as "nothing
  draws" — with no FNA3D error and no managed exception. Judge the GL path on real hardware only.

  Two red herrings, both of which cost time on 2026-08-31 and neither of which is the renderer:
  the `E libEGL: called unimplemented OpenGL ES API` flood is emitted **once per second by a
  `.NET TP Worker` thread**, not per frame by `SDLThread` — that is the emulator's per-thread GL
  dispatch stubbing out a call made with no current EGL context, and it appears under Vulkan's
  absence too. And a white screen is not necessarily a dead one: `wpr_game_debug.log` showed
  `GraphicsDevice.Clear ... color=(1.00,1.00,1.00,1.00)` repeating, i.e. the game clearing happily.

**Which driver a launch gets is decided at runtime, not by the env file alone** (2026-08-31). The
env var forcing OpenGL is still the default, but `GameActivity.SDLMain` calls
`Graphics.GraphicsDriverPolicy.Apply(...)` before the host builds the game, and that relaxes the
force on the emulator so it renders (verified: full Mirror's Edge menu, `FNA3D Driver: Vulkan`, and
the libEGL flood at zero). Physical devices take a deliberate **do-nothing** branch and keep the
forced OpenGL path.

- The lever is `WPR.Backend.FNA.GraphicsDriverSelection.Apply(name)` —
  `SDL_SetHintWithPriority(..., SDL_HINT_OVERRIDE)`, the one thing that beats the process env var
  (`SDL_GetHint` consults its hint list first and an OVERRIDE-priority hint wins). Pass **null**,
  never `""`, to restore automatic order: an empty string is still non-NULL to `SDL_GetHint` and
  would `strcmp` against every driver name and match none.
- Policy lives in the head (`Graphics/GraphicsDriverPolicy.cs`), plumbing in the backend. "Is this
  an emulator" is not something a graphics backend should reason about.
- **Keep the failure direction.** Detection is biased to false negatives on purpose: a missed
  emulator only means the emulator renders nothing, whereas a false positive puts a real phone back
  on the T-posing Vulkan driver. Never invert this into "force OpenGL only when we detect hardware".
- **A declined driver falls through by name, it does not throw** (2026-08-31).
  `SDL2_FNAPlatform.PrepareWindowAttributesWithFallback` walks an explicit ladder — the requested
  driver first, then `OpenGL`, `Vulkan`, then automatic — logging each attempt. The earlier shape
  ("on failure clear the force and let FNA3D choose") crashed a user's device with
  `No supported FNA3D driver found!`, for two reasons worth not re-introducing: **automatic order is
  not a safety net on Android**, because FNA3D offers OpenGL first and it is declined there, so
  "automatic" effectively means Vulkan — walking away from the one driver that may work; and
  `IsDriverForced()` read the hint, which the head legitimately clears (emulator policy, the
  override file), so a failure looked like "nothing was forced, nothing to fall back to" and
  rethrew. **Whatever was requested stays first in the ladder — including "automatic"**, or the
  fallback silently overrules the emulator policy and puts it back on the non-rendering GL path.
- No rebuild needed to re-test a driver: a `fna3d_driver.txt` next to the app's external files
  (containing `OpenGL`, `Vulkan` or `auto`) overrides both branches. Useful for a phone whose GL
  driver misbehaves.

  ```powershell
  adb shell "echo Vulkan > /storage/emulated/0/Android/data/com.wpr.android/files/fna3d_driver.txt"
  ```

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
FNA3D's unfinished Vulkan driver meeting the emulator's unfinished Vulkan driver, and it is
unreachable on hardware, where `fna3d.env` forces OpenGL. **Treat "renders on the emulator" as a
smoke test only: a game that draws its menus there may still die the moment it draws its actual
scene, and neither crash says anything about the device.**

**Only the emulator could ever hit it, and only in Debug**, which is why it went unnoticed: a
physical device keeps `fna3d.env`'s forced OpenGL and never creates a Vulkan instance at all, while
`GraphicsDriverPolicy` clears that force on an emulator so it renders. The layer is then requested
because `GraphicsDevice`'s ctor passes `debugMode = 1` under `#if DEBUG`, and FNA3D asks for
`VK_LAYER_KHRONOS_validation` whenever `debugMode` is set. Read the two lines together in logcat:

```
FNA3D Driver: Vulkan
Vulkan validation enabled! Expect debug-level performance!
```

Removing the binaries rather than passing `debugMode = 0` on Android is deliberate: `debugMode`
also turns on the OpenGL driver's `GL_KHR_debug` callbacks, which are the useful half on the driver
real devices actually run. Dropping the layer disables Vulkan validation *only*.

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

**This is a patcher table change: `ApplicationPatcher.Version` is 22 and affected games must be
repatched.** Unlike v21 it is not identity-binding — a v21 install still launches, it just keeps
failing to build the affected serializer — so `--repatch-installed` is enough.

### A suppressed draw starves FNA3D's off-thread command queue (2026-09-01)

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
[wpr-platform] Android: accelerometer=AndroidAccelerometerProvider driver=OpenGL audio=[AndroidMediaPlayer] transcoder=RemoteAudioTranscoder achievements=EfAchievementStore notifications=AndroidNotificationManager tilt=none
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
**`ApplicationPatcher.Version` is 21 and every installed game must be repatched or reinstalled** —
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

**The lever is `GameLifecycleQuirks`** (`Src/Backends/WPR.Backend.FNA/`) — a ProductId table read
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
achievements or the keyboard tilt emulator. `FNA3D_FORCE_DRIVER=OpenGL` in the parent shell is
inherited by the child, which is how the Android-only GL path gets reproduced on Windows.

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

**This was a patcher table change (v20).** The current version is **21** — see "The XNA spine is
WPR-owned" above, which supersedes this paragraph's version number; the v20 note below still
describes what v20 itself changed. Unlike v19 it is not identity-binding — a v19 install still launches, it
just keeps the exclusive share and keeps failing to save. `--repatch-installed` is enough (it
restores each `.dll.original` first, so repatching is idempotent).

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
dotnet build <project>.csproj -c Debug -f net8.0-windows10.0.17763.0 \
    -maxcpucount:1 -nodeReuse:false --nologo -p:SolutionDir=<repo>/Src/
```

- `-f net8.0-windows10.0.17763.0` pins the desktop leg explicitly.
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
