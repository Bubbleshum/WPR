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

- **Build**: the user clicks build/run in Rider, which builds `WPR.UI.Desktop`
  for `net8.0-windows10.0.17763.0` and pulls in the rest by project reference.
- **Run**: `WPR.UI.Desktop` is the entry point. The UI lists installed games;
  picking one launches via `SilverlightLauncher.LaunchAsync` or
  `XnaLauncher.LaunchAsync`.
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
   `WPR.XnaCompabilityPatch/*.cs`, `WPR.Framework.Xna/*.cs`, GamerServices,
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
- Renderers (`SilverlightRenderer.cs`, `D3D11ImageSplashRenderer.cs`, …)
- Pointer-to-gesture bridge (`Gestures.cs`, `PanoramaState.cs`,
  `PanoramaStateTable.cs`, `PanoramaSelectedItemSync.cs`)
- XAML helpers (`XamlTypeConverter.cs`, `MarkupExtensionParser.cs`)
- Hosting glue (`HostContext.cs`, `HitTester.cs`, `BingWallpaper.cs`,
  `ResourceBundleReader.cs`, `GameMakerAssetExtractor.cs`)
- Theme constants (`PhoneTheme.cs`)
- `AssemblyInfo.cs`

If you're adding something that *is* a shim, it goes in the namespace tree.
If you're adding new hosting logic, it stays at the root.

`WPR.WindowsCompability` and `WPR.XnaCompabilityPatch` (which builds the assembly
`WPR.XnaCompability` — another project-name/assembly-name mismatch) are still flat.
The mirror-tree convention has only been applied to `WPR.Framework.Silverlight` so
far. Apply the same pattern when you next touch those projects, but don't make a
separate pass just to reorganise them.

### CLI build shortcuts that work

Full solution builds hit `NU1202` on `Avalonia.Android` (workload-version
mismatch — see Environment notes). When verifying a small edit:

```
dotnet build <project>.csproj -c Debug -f net8.0-windows10.0.17763.0 \
    -maxcpucount:1 -nodeReuse:false --nologo
```

- `-f net8.0-windows10.0.17763.0` skips the broken Android leg.
- `-maxcpucount:1 -nodeReuse:false` avoids the parallel-build CS0006
  "metadata file not found" race that hits in MSBuild's default settings.
- Build leaf projects first (e.g. `WPR.Framework.Silverlight`) — they have
  no project deps that need staging and give the fastest yes/no on a shim edit.
- Building `WPR` / `WPR.UI` / `WPR.UI.Desktop` from CLI often fails with
  spurious "namespace not found" cascades because the CLI doesn't restage
  transitive project references the way Rider does. **Treat a successful
  leaf-project build as sufficient validation; defer the full chain to
  Rider.** If the user reports a runtime error after their next IDE build,
  that's the real signal.

### Verifying a patcher entry took effect

If the user says "still the same error after reinstall," check whether
`ApplicationPatcher.PatchDll` actually wrote a `.dll.original` sibling next to
the user assembly in the per-game install dir (its path is built in
`ApplicationInstaller.CreateApplicationEntryAndExtract`; ask the user for the
exact folder once and stash it for the session). If the `.original` is older
than the patcher source changes (or missing entirely), the install didn't
re-run — the user may have hit "launch" instead of "reinstall," or the install
dir wasn't cleared.

## Cleanup at end of session

Before finishing a task, clean up anything created for diagnosis/verification
that isn't part of the change itself:

- **Log files**: anything I wrote into the repo root or under `Src/` for build
  capture (e.g. `build_*.log`, `restore_*.log`, `install_*.log`). Leave
  pre-existing files alone — only remove ones I authored this session.
- **Stray scratch csprojs**: anything I created purely to probe SDK behavior
  should be removed before declaring done. (The repo-root `global.json`
  pinning to 8.0.421 is **not** scratch — it's part of the committed build
  config; leave it.)
- **Build processes**: check for orphaned `dotnet` / `MSBuild` /
  `VBCSCompiler` instances I spawned (`Get-Process` + match command line).
  Do **not** kill processes belonging to Rider (`ReSharperHost`,
  `JetBrains.*`) or Visual Studio (`devenv`) — those are the user's IDE.
- **`obj/` and `bin/`**: leave these. They're normal incremental-build
  artifacts; removing them would force a rebuild the user didn't ask for.

## Building the Android leg (WPR.UI.Android)

The Android project now builds from both Rider and the CLI. The setup is:

- **`global.json`** at the repo root pins the SDK to **8.0.421** (`rollForward: latestFeature`).
  Without this, MSBuild picks the .NET 10 SDK and loads only the .NET 10 Android workload
  manifest, which doesn't ship `net8.0-android*` ref packs → `Mono.Android.dll` doesn't
  resolve and you get CS0234 on `Android.Content` / `Android.Graphics` / `AssetManager`.
- **.NET 8 SDK + android workload** are installed system-wide at `C:\Program Files\dotnet`
  alongside .NET 10. The android manifest lives at
  `C:\Program Files\dotnet\sdk-manifests\8.0.100\microsoft.net.sdk.android\`.
- **Android SDK platforms** for API 34 live at `C:\Users\BenSl\AppData\Local\Android\Sdk`
  (user-local; Android Studio install). The system Android SDK at
  `C:\Program Files (x86)\Android\android-sdk\platforms\` only has android-35/36.
  Set `ANDROID_HOME` / `ANDROID_SDK_ROOT` user env vars to the user-local path so
  MSBuild finds API 34.

### Rider — works out of the box now
With the `global.json` committed and the env vars set, Rider builds Android cleanly.
Verify with `& "C:\Program Files\dotnet\dotnet.exe" --version` from the repo root — it
must print `8.0.421`. If it prints `10.0.x`, the `global.json` isn't being picked up.

### CLI build recipe (still useful for headless verification)

```powershell
$env:ANDROID_HOME      = "C:\Users\BenSl\AppData\Local\Android\Sdk"
$env:ANDROID_SDK_ROOT  = $env:ANDROID_HOME
$env:JAVA_HOME         = "C:\Program Files\Android\Android Studio\jbr"
& "C:\Program Files\dotnet\dotnet.exe" build `
    "Src\UI\WPR.UI.Android\WPR.UI.Android.csproj" `
    -c Debug -maxcpucount:1 -nodeReuse:false --nologo `
    -p:AndroidSdkDirectory="$env:ANDROID_HOME"
```

Output: `Src\UI\WPR.UI.Android\bin\Debug\net8.0-android34.0\com.wpr.android-Signed.apk` (~31 MB).

### .NET / Android API mapping (Microsoft locked these)
- `net8.0-android*` → API **34** only. There is no `net8.0-android35.0`.
- `net9.0-android*` → API **35** only.
- `net10.0-android*` → API **36** only.

Also: `Avalonia.Android` skipped .NET 9. Version 11.x ships only `lib/net8.0-android34.0/`;
12.x ships only `lib/net10.0-android36.0/`. To move off API 34 you must move all the way
to net10 + Avalonia 12.

### What changed (history note — 2026-05-25)
Earlier CLAUDE.md said .NET 8 SDK was only present at user-local
`C:\Users\BenSl\.dotnet`, that Rider couldn't see it, and that `global.json` pinning
would fail with NETSDK1141. That was true when written. Since then .NET 8 SDK +
android workload were installed system-wide, so `global.json` now resolves cleanly to
the system .NET 8 install. The user-local `C:\Users\BenSl\.dotnet` is currently
**partially broken** (android manifest gone, maui manifest references it) and should
not be used until reinstalled.

## Environment notes (as of 2026-05-25)

- System .NET SDKs: `C:\Program Files\dotnet\sdk\` — **8.0.421** and **10.0.203**
  side-by-side. `global.json` at the repo root pins the build to 8.0.421.
- System workload manifests at `C:\Program Files\dotnet\sdk-manifests\`:
  - `8.0.100\microsoft.net.sdk.android` — android workload `34.0.154/8.0.100`
    (the one that matters for this repo)
  - `10.0.100\android` — android workload `36.1.53/10.0.100` (unused; .NET 10
    targets `net10.0-android36.0` only and the repo doesn't use that TFM)
- User-local .NET SDK at `C:\Users\BenSl\.dotnet` (8.0.420/8.0.421): **currently broken**
  — `microsoft.net.sdk.android` manifest is missing, `microsoft.net.sdk.maui` references
  it. Reinstall via `dotnet-install.ps1` if you need it back; otherwise ignore.
- Android SDK platforms:
  - `C:\Program Files (x86)\Android\android-sdk\platforms\`: **android-35**, **android-36** (no API 34).
  - `C:\Users\BenSl\AppData\Local\Android\Sdk\platforms\`: **android-34**, **android-36.1**.
  Build needs API 34, so set `ANDROID_HOME` / `ANDROID_SDK_ROOT` to the user-local path
  (or pass `-p:AndroidSdkDirectory=...` on the CLI).
- With the `global.json` pin in place, `net8.0-android` and `net8.0-android34.0` both
  resolve to the .NET 8 android workload's API 34 ref pack. `SupportedOSPlatformVersion`
  can range from `21.0` up to `34.0` (must be ≤ TPV; NETSDK1135 fires if higher).
