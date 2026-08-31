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

### Platform input: sensors are behind a seam, everything else already lives in a head

Motion input follows the same three-part shape as achievements (2026-08-30):

* **Contract** — `WPR.Abstractions.Sensors.ISensorProvider`. It speaks
  `System.Numerics.Vector3` on purpose: the WP7 vocabulary (`AccelerometerReading`, the XNA
  `Vector3`) lives in the assemblies that *consume* this contract, so using it here would
  cycle. That is the same reasoning that put `IInputBackend` in `WPR.Xna.Rhi` — the difference
  is that a motion sample is three floats, so a neutral type costs one conversion instead of a
  whole vocabulary.
* **Registry** — `WPR.Sensors.SensorBackend`, inside the `Microsoft.Devices.Sensors` assembly
  beside its consumer, exactly like `SilverlightBackend`. Deliberately in a `WPR.*` namespace,
  not `Microsoft.Devices.Sensors`: that namespace is the WP7 contract games bind by identity.
  It is **not** cleared at teardown (the provider is launcher-lifetime); what *is* cleared is
  the subscriber list, via `ISensorProvider.ResetForNewLaunch()` from `ResetWprSingletons`.
  Skipping that reset reintroduces the 2026-08-08 ALC leak.
* **Implementations** — `WPR.Platform.Windows/Input/WindowsSensorProvider.cs` (over the
  `KeyboardAccelerometerHost` keyboard emulator) and
  `WPR.Platform.Android/Input/AndroidSensorProvider.cs` (Xamarin.Essentials, the package's only
  consumer in the repo). Both registered in their head's `ServicesSetup.Start()`.

`Start/StopAccelerometer` are **counted, not idempotent**. One provider is shared by every
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

**Songs do not go through FAudio on Android.** `FnaGameHost` composes the `IMediaBackend` slot per
game launch, but it asks `WPR.Backend.FNA.MediaBackendOverride.Create()` rather than newing
`FnaMediaBackend` directly, and the Android head registers
`AndroidMediaBackend` (platform `Android.Media.MediaPlayer`) through
`MediaBackendOverride.SetFactory(...)` in `ServicesSetup.Start()`. An *override factory* rather
than a plain registry because that slot is per-launch and cleared on teardown — a head that called
`XnaBackend.SetMedia` at startup would be overwritten by the next launch.

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

Two things about `AndroidMediaBackend` worth knowing: it delegates the **entire video half** to
`FnaMediaBackend` (Theorafile is fine and shouldn't be duplicated), and it relies on MediaPlayer
sniffing **content, not extension** — our transcoded songs keep the `.wma` filename. That
assumption is verified: logcat shows `allocate(c2.android.vorbis.decoder)` and
`read media type: audio/vorbis` on a `.wma`-named file. If songs ever silently fail to start,
re-check that first.

**Owning the song player means owning its lifecycle — three things FAudio used to do for free.**
Each of these was a real defect, not a hypothetical:

- **Nothing else will stop the music.** Sound effects go quiet when the app backgrounds because SDL
  pauses FAudio's audio device as part of the Android activity lifecycle; a platform `MediaPlayer`
  is ours alone and played on over the home screen. `GameActivity.OnPause`/`OnResume` now call
  `AndroidMediaBackend.SuspendForBackground()`/`RestoreFromForeground()`. It claims only a song that
  was *actually playing*, so a game that paused or stopped its own music on deactivation keeps that
  state instead of having music restarted under its pause menu.
- **A paused player may not survive the background.** Android can reclaim the audio track while the
  app is away, so `ResumeSong` cannot just call `Start()` — it captures the offset on every pause
  and rebuilds the player with `SeekTo` if the resume throws. Swallowing that exception (the
  original code) silently killed music for the rest of the session, because nothing upstream ever
  re-issues `PlaySong` for a song it believes is merely paused.
- **An errored player never raises Completion**, so `_ended` must be latched from the `Error`
  callback too, or the XNA queue polls `GetSongEnded()` forever and every later track is lost.

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

**Use ffmpeg-kit's ASYNC entry point (`ExecuteWithArgumentsAsync` + a completion callback), never
the synchronous one.** Both failure modes of the sync API were observed on the emulator and neither
is obvious from a build:

| how the sync API was called | what happened |
| --- | --- |
| on the UI thread (the natural result of `await`ing an install started from an activity) | works, but the main thread sits in `sem_wait` inside `libmonosgen` for the whole soundtrack — a multi-minute ANR, "WPR isn't responding" |
| on a .NET thread-pool thread (`Task.Run`) | **never returns.** ffmpeg emits no session log at all, the conversion stalls on the first file, and the app stays responsive — so it looks like a hang with no error anywhere |

The async overload hands the work to ffmpeg-kit's own executor and returns immediately;
`FFmpegKitAudioTranscoder` bridges its callback to a `TaskCompletionSource`. With that, 36 tracks
convert in under 25 s on an x86_64 emulator, versus ~10 s *per file* on the UI-thread path.

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
`UnityPortLauncher`), the tilt stack (`KeyboardTiltBinding`, `TiltOverlay`,
`TiltInputXnaComponent`, `TiltOverlayXnaComponent` — since 2026-08-30 under
`Input/`, namespace `WPR.Platform.Windows.Input`) and `PhoneHardwareButtons` /
`WP7AccentColors` all went to `WPR.Platform.Windows`, because the Android shell is
native and used none of them. `PixelToGridLengthConverter`, `ProgressView`,
`RegistrationPage`, `RegistrationService` and `System.Windows.MessageBoxButton`
were deleted — nothing referenced them.

Six files were needed by **both** heads, so each head now owns a copy:

| file | note |
| --- | --- |
| `ApplicationLaunchRequest.cs` | Android copy keeps the `WPR.Common.Log` call the `#if __ANDROID__` block used to guard |
| `LocaleUtils.cs` | identical apart from namespace |
| `MessageBoxUtils.cs` | the two halves of the old `#if __ANDROID__` file: Avalonia windows on Windows, `AlertDialog` on Android |
| `ServicesSetup.cs` | **no longer identical.** Each head is the composition root and registers its own platform implementations — since 2026-08-30 that includes `SensorBackend.SetProvider(...)`, which is `WindowsSensorProvider` (keyboard emulator) on one side and `AndroidSensorProvider` (hardware) on the other, and since 2026-08-31 `AudioTranscoderBackend.SetTranscoder(...)` — `FFMpegCoreAudioTranscoder` (spawns `ffmpeg.exe`) vs `FFmpegKitAudioTranscoder` (JNI). The `Guide`/`MessageBox` half still tracks line for line |
| `System/Windows/MessageBox.cs` | internal placeholder `ShowSimpleImpl` holder |
| `Properties/Resources.resx` + `Resources.Designer.cs` | the localized launcher strings; the designer's `ResourceManager` name follows each head's `$(RootNamespace)` |

**Change one, change the other.** Nothing enforces it — a divergence compiles
fine and only shows up at runtime on one platform.

Namespaces follow the head: `WPR.UI` → `WPR.Platform.Windows` /
`WPR.Platform.Android`. Note that inside `namespace WPR.Platform.Android`, the
identifier `Android` binds to *that* namespace, not the Mono.Android root — so
Android-copy code writes `global::Android.Resource.String.Ok`. The rest of the
existing Android files already do this; match them.

Avalonia `avares://` URIs use the **assembly** name, not the project name:
`avares://WPR.Platform.Windows/Themes/Brand.axaml`.

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

17 projects target `net8.0-android`, 14 of them multi-targeting (the `WPR.UI`
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
  the filter drops `WPR.Platform.Android`, the Java bindings and
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
