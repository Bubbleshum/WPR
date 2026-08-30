# Building WPR

The full build reference. If you just want to get the desktop app running, the
three commands in the [README](../README.md#build-it-yourself) are enough — this
document is for everything past that.

- [Prerequisites](#prerequisites)
- [Desktop](#desktop)
- [Run configurations](#run-configurations)
- [Building without the Android workload](#building-without-the-android-workload)
- [Android](#android)
- [Packaging scripts](#packaging-scripts)
- [Tests](#tests)
- [Troubleshooting a fresh clone](#troubleshooting-a-fresh-clone)

Publishing a release is covered separately in [RELEASING.md](RELEASING.md).

---

## Prerequisites

| Needed | For | Notes |
| --- | --- | --- |
| **.NET SDK 8.0** | everything | `global.json` pins the build to the **8.0** feature band (`rollForward: latestFeature`), so any `8.0.1xx`+ SDK works. A machine with only .NET 9/10 will not build this repo. [Download](https://dotnet.microsoft.com/download/dotnet/8.0). |
| **Windows 10 1809 (build 17763) or newer** | the desktop app | The desktop TFM is `net8.0-windows10.0.17763.0`. No Windows SDK install is needed — the ref pack comes from NuGet. |
| Rider 2023.3+ / Visual Studio 2022 17.8+ | IDE workflow | Optional; the CLI build below is enough. |
| .NET **Android workload**, JDK 17+, Android SDK **API 34** | the Android APK **only** | Skip all of this if you only care about desktop — see [Building without the Android workload](#building-without-the-android-workload). |

Everything else the app needs at runtime — `SDL2.dll`, `FNA3D.dll`, `FAudio.dll`,
`FNWP72.dll`, `ffmpeg.exe`, the prebuilt SQLite databases, the vendored
`FNA.Platform` and `Icons.Avalonia` sources — is committed to the repo. There are
no submodules and no `git lfs` step: a plain `git clone` is a complete checkout.

## Desktop

```bash
git clone https://github.com/Bubbleshum/WPR.git
cd WPR
dotnet build Src/Platforms/WPR.Platform.Windows/WPR.Platform.Windows.csproj -c Debug
```

The exe lands in
`Src/Platforms/WPR.Platform.Windows/bin/Debug/net8.0-windows10.0.17763.0/WPR.Platform.Windows.exe`.

Or use the packaging script, which publishes into `Artifacts/` and can launch it
for you:

```pwsh
.\build-desktop.ps1 -Configuration Debug -Run
```

**In an IDE, open [`Src/WPR.Windows.slnf`](../Src/WPR.Windows.slnf)** — a solution
filter over `WPR.sln` holding the 19 projects the desktop app needs. It leaves out
`WPR.Platform.Android`, the four Java-binding projects and `assembly-store-reader`,
all of which require the Android toolchain. Build → run `WPR.Platform.Windows`.
For Android, open [`Src/WPR.Android.slnf`](../Src/WPR.Android.slnf) instead.

Open the full `Src/WPR.sln` only when you have the Android toolchain installed;
otherwise "Build Solution" will fail on the Android-only projects even though the
desktop app itself builds fine.

### CLI build for a quick edit-verify loop

To verify a small edit on a leaf project:

```pwsh
dotnet build <project>.csproj -c Debug `
    -f net8.0-windows10.0.17763.0 `
    -maxcpucount:1 -nodeReuse:false --nologo `
    -p:SolutionDir=<repo>/Src/
```

- `-f net8.0-windows10.0.17763.0` pins the desktop leg and skips the Android one.
- `-maxcpucount:1 -nodeReuse:false` avoids an MSBuild `CS0006`
  "metadata file not found" race that hits in the default parallel settings.
- `-p:SolutionDir=<repo>/Src/` — **forward slashes and a trailing slash**. Many
  csprojs resolve `ProjectReference`s through it. `build-desktop.ps1` passes it
  for you.

## Run configurations

Both mechanisms below are version-controlled and portable — no absolute paths, so
they work on any clone. Open the solution and these are already in the run
dropdown:

| Configuration | What it does | Comes from |
| --- | --- | --- |
| **WPR.Platform.Windows** | Runs the desktop app. This is the one you want ~95% of the time. | `launchSettings.json` |
| **Repatch installed games** | Re-runs the current patcher over every installed game, headless, then exits. Run this after changing `ApplicationPatcher` — see [Reinstall vs. rebuild](ARCHITECTURE.md#reinstall-vs-rebuild). **Close WPR first**, or the installed DLLs are locked. | `launchSettings.json` |
| **Reinstall all library games** | The above, plus a fresh install of every XAP in your library folder that isn't installed yet. Also needs WPR closed. | `launchSettings.json` |
| **WPR Desktop (native debugging)** | The desktop app with mixed-mode debugging, for crashes that land inside `SDL2.dll` / `FNA3D.dll` / `FAudio.dll` / `FNWP72.dll`. Slower to start, so it's a separate entry. | `Src/.run/` |
| **WPR Android (needs android workload)** | Builds and deploys the APK to the selected device or emulator. | `Src/.run/` |

- [`Src/Platforms/WPR.Platform.Windows/Properties/launchSettings.json`](../Src/Platforms/WPR.Platform.Windows/Properties/launchSettings.json)
  holds the three desktop profiles. Rider and Visual Studio both surface these
  automatically, and they work from the CLI too:
  ```bash
  dotnet run --project Src/Platforms/WPR.Platform.Windows --launch-profile "Repatch installed games"
  ```
- [`Src/.run/`](../Src/.run) holds Rider's shared run configurations — the two
  things a launch profile can't express. Rider reads `*.run.xml` from a `.run`
  folder in the **solution** directory, which is why it lives under `Src/` rather
  than at the repo root. `.idea/` is deliberately gitignored (it carries
  per-machine MSBuild paths), so `.run/` is the only sharable location.

The Android entry only resolves when `Src/WPR.sln` or `Src/WPR.Android.slnf` is
open — `WPR.Windows.slnf` leaves `WPR.Platform.Android` out on purpose, so the
entry shows as broken there. That's expected, not a bad checkout.

A .NET run configuration cannot pass MSBuild properties, so there is no run
configuration for `-p:IncludeAndroidTargets=false`. You don't need one — the
detection in `Src/Directory.Build.targets` already handles a machine without the
workload (next section). Use `build-desktop.ps1` if you want to force it.

## Building without the Android workload

Fourteen projects in this repo carry a `net8.0-android` target framework, twelve
of them alongside a desktop TFM — and MSBuild builds *every* TFM of a project
reference. Left alone that means a clone with a plain .NET 8 SDK cannot build the
desktop app at all: the Android leg fails with `NETSDK1147: the following
workloads must be installed: android` and takes the whole build down with it.

[`Src/Directory.Build.targets`](../Src/Directory.Build.targets) fixes that. It
checks for the API-34 **reference pack**
(`<dotnet-root>/packs/Microsoft.Android.Ref.34`, installed by
`dotnet workload install android`) and, when it isn't there, strips `*-android`
out of `$(TargetFrameworks)` repo-wide. Desktop contributors need nothing beyond
the .NET 8 SDK; machines that *do* have the workload build exactly as before.

> The check deliberately tests for `Microsoft.Android.Ref.34` specifically rather
> than globbing `Microsoft.Android.Sdk.*`. Workload installs land in whichever SDK
> band resolves at the time, and bands don't share packs — a glob happily reports
> "installed" from a .NET 10 band install while the 8.0 band has nothing, and then
> every Android leg fails `NETSDK1147` for real. The comments in that file spell
> out the two traps in more detail; read them before editing it.

The desktop build prints one line when the strip kicks in. To override the
detection either way:

```bash
dotnet build ... -p:IncludeAndroidTargets=true    # force the android leg on
dotnet build ... -p:IncludeAndroidTargets=false   # force it off
```

`build-android.ps1`, `build-android.sh` and the release workflow all pass `true`,
so a detection miss can never silently skip the Android build in CI.

## Android

On top of the .NET 8 SDK you need the workload — installed **from the repo root**,
so `global.json` pins it to the 8.0 band:

```bash
dotnet workload install android
```

plus a JDK (17 or newer; 21 is what's currently tested) and Android SDK
**platform 34**. `net8.0-android*` maps to API 34 and only API 34:

| .NET TFM | Android API |
| --- | --- |
| `net8.0-android*` | 34 (there is no `net8.0-android35.0`) |
| `net9.0-android*` | 35 |
| `net10.0-android*` | 36 |

`Avalonia.Android` also skipped .NET 9 — 11.x ships only `lib/net8.0-android34.0/`
and 12.x only `lib/net10.0-android36.0/`, so moving off API 34 means moving all
the way to net10 + Avalonia 12.

Point the build at an SDK that actually has API 34 installed:

```pwsh
$env:ANDROID_HOME     = "C:\Android\Sdk"        # wherever yours lives
$env:ANDROID_SDK_ROOT = $env:ANDROID_HOME
$env:JAVA_HOME        = "C:\Program Files\Microsoft\jdk-21.0.12.8-hotspot"
```

Then:

```pwsh
.\build-android.ps1          # -> Artifacts\android\Release\com.wpr.android-Signed.apk
```

`build-android.sh` is the Linux equivalent. A plain `dotnet build -c Release` on
the Android project stops short of packaging, so both scripts add
`-t:SignAndroidPackage`. The APK is signed with the local **debug keystore** —
fine for sideloading, not for a Play upload. See [RELEASING.md](RELEASING.md#android-signing)
for a real key.

### Deploying to a device or emulator

```pwsh
Start-Process "C:\Android\Sdk\emulator\emulator.exe" -ArgumentList "-avd","<your-avd>"
C:\Android\Sdk\platform-tools\adb.exe wait-for-device
C:\Android\Sdk\platform-tools\adb.exe install -r -t "Src\Platforms\WPR.Platform.Android\bin\Debug\net8.0-android34.0\com.wpr.android-Signed.apk"
C:\Android\Sdk\platform-tools\adb.exe shell monkey -p com.wpr.android -c android.intent.category.LAUNCHER 1
C:\Android\Sdk\platform-tools\adb.exe logcat -d | Select-String "WPR|FATAL"
```

A healthy start logs `WPR: MainActivity OnCreate completed (native shell)`. The
launcher activity is `com.wpr.android/.MainActivity`. An API 34 APK runs fine on a
newer device or emulator image.

The Debug APK is around 200 MB (`EmbedAssembliesIntoApk` +
`AndroidEnableAssemblyCompression=false` + the bundled achievement catalogues).

## Packaging scripts

Two repo-root PowerShell scripts produce a runnable artifact without opening the
IDE. Both default to **Release** and write into
`Artifacts/<target>/<Configuration>/` (gitignored).

```pwsh
.\build-desktop.ps1          # -> Artifacts\desktop\Release\WPR.Platform.Windows.exe
.\build-android.ps1          # -> Artifacts\android\Release\com.wpr.android-Signed.apk
```

Useful switches:

| Script | Switches |
| --- | --- |
| `build-desktop.ps1` | `-Configuration Debug`, `-OutputDir`, `-SelfContained` (bundles the .NET runtime), `-NoPublish` (plain build, output stays in `bin\`), `-Clean`, `-Run` |
| `build-android.ps1` | `-Configuration Debug`, `-TargetFramework`, `-OutputDir`, `-Clean`, `-Install` (`adb install -r` to a connected device) |

Both pass `-p:SolutionDir=` explicitly. That is now belt-and-braces rather than
load-bearing: `Src/Backends/FNA.Platform/Directory.Build.props` shadows the `Src/`
one that defines `SolutionDir` (nearest-wins), and it now imports the parent, so
`FNA.Core.csproj` resolves `$(SolutionDir)Core\WPR.Framework.Xna` on its own
instead of cascading into `CS0246` on every XNA type.

## Tests

```bash
dotnet test Src/Tests/WPR.Tests/WPR.Tests.csproj -c Debug
```

`BackendIsolationTests.Backend_references_match_documented_baseline` is the
architecture-migration guard: it fails both when a *new* assembly starts
referencing FNA/Vortice **and** when a stage removes an existing leak (so the win
gets locked into the baseline). It reads built assemblies, so build the whole
solution in the IDE first. See [`Plans/STAGE-GATE.md`](../Plans/STAGE-GATE.md) for
the per-stage exit checklist.

## Troubleshooting a fresh clone

| Symptom | Cause / fix |
| --- | --- |
| `NETSDK1045` / "compatible SDK version was not found" | No .NET **8** SDK installed. `global.json` deliberately refuses to roll forward to 9/10 — the Android workload only exists for 8 here. Install the 8.0 SDK. |
| `NETSDK1147: the following workloads must be installed: android` | The Android TFM gating did not kick in. Force it off with `-p:IncludeAndroidTargets=false`, and open an issue with your `dotnet --info` output. |
| `CS0246` on every XNA type when building from the CLI | `$(SolutionDir)` did not resolve. Add `-p:SolutionDir=<repo>/Src/` (forward slashes, trailing slash). |
| `CS0006` "metadata file not found", intermittent | MSBuild parallel-build race. Add `-maxcpucount:1 -nodeReuse:false`. |
| `NU1301` unable to load the service index | A NuGet source in `Src/NuGet.Config` is unreachable. That file `<clear />`s the source list, so every entry must resolve. |
| Rider builds with the wrong MSBuild / "build tool not found" | Stale `Src/WPR.sln.DotSettings.user`. It is no longer tracked; delete your local copy, or clear Settings → Build → *Use MSBuild version / Custom build tool path*. |
| CS0234 on `Android.Content` / `Android.Graphics` / `AssetManager` | MSBuild picked the .NET 10 SDK, whose Android manifest has no `net8.0-android*` ref packs. Check `dotnet --version` from the repo root prints `8.0.4xx` — if not, `global.json` isn't being picked up. |
| A game still fails with the old error after a reinstall | Check whether `ApplicationPatcher.PatchDll` actually wrote a `.dll.original` sibling in `%LocalAppData%\WPR\AppData\<ProductId>`. If it's missing or older than your patcher edit, the install didn't re-run — "Play" was clicked instead of "Repatch"/"Reinstall". |
