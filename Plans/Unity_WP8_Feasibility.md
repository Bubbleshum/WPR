# Unity Windows-Phone titles — Feasibility & the Port Rail

**Status:** Scoping study + implemented launcher rail (no per-game port yet).
**Verdict:** A Unity WP title **cannot be hosted in-process** by WPR — its engine is native ARM
code we can't execute, same wall as AC Pirates' Spark2. **But**, unlike Spark2, a Unity title is
fully *recoverable*: assets are open, scripts are managed, and Unity's own tooling rebuilds the
game for desktop and Android. So the supported path is a **one-time per-game rebuild** (via
AssetRipper) into standalone binaries that WPR **launches** rather than hosts. This doc records
why, the port recipe, and the launcher rail that's now in the codebase.

> **Update 2026-10-12 (track 4 spike, §11):** the "can't execute ARM" premise is obsolete. Dynarmic
> maps `UnityPlayer.dll` and `SparkApp.dll`, and both DllMains run. Spark2 is now a **go** through
> the existing component-title host. Unity stays **no-go** in-process, because its managed/native
> binding assumes one shared 32-bit address space, so the port rail below is still the path for
> Unity.

First worked example: **Twins Minigame** (`GAME TROOPERS`, product `19e9713c-…`), Unity `4.6.9f1`.

---

## 1. What a Unity WP title actually is (evidence, from Twins)

| Fact | Evidence |
|---|---|
| Engine is **native ARMv7** | `UnityPlayer.dll` in the XAP is PE machine `0x1c4` (ARMv7-THUMB2), 8 MB. WPR's installer stubs it to a synthesized x86 shim (`UnityPlayer.dll.native_original` keeps the real one). |
| Silverlight shell is **just a host** | `Twins.dll` `MainPage` does: `new WinRTBridge` → `UnityApp.SetBridge` → `DrawingSurfaceBackground.SetBackgroundContentProvider(UnityApp.GetBackgroundContentProvider())`. `UnityApp` lives in the native player. |
| Game logic is **managed .NET** | `Assembly-CSharp.dll` holds the MonoBehaviours (`GameScript`, `PlayerScript`, `SceneScript`, …) — decompilable, **un-obfuscated**. |
| …but bound to native via **CppInvoke** | `UnityEngine.dll` methods carry `UnityEngine.CppInvokeAttribute`; every engine call marshals into the native player through the bridge. |
| Assets are **open, unencrypted** | `Data/` has standard `level0-2`, `mainData`, `sharedassets*.assets`, `resources.assets`, `unity default resources`. Version string `4.6.9f1` sits in plaintext. Fully readable by AssetRipper / AssetStudio / UnityPy. |

### Why it shows the "no d3d page"
`UnityApp.GetBackgroundContentProvider()` on the stubbed player returns `null` →
`DrawingSurfaceBackgroundGrid.SetBackgroundContentProvider(null)` → `SilverlightRenderer`
paints `DrawD3DPlaceholder` ("(no Direct3D content)"). That striped screen *is* the shell
running correctly with no engine behind it.

## 2. Why in-process hosting is closed (same as Spark2)

- **Run the native engine** — impossible: ARM machine code can't execute on x64.
- **Reimplement the engine** — running `Assembly-CSharp.dll` means implementing the whole
  `UnityEngine` CppInvoke surface (tens of thousands of symbols) = reimplementing Unity 4.6.
  Out of scope.
- **Drop a desktop `UnityPlayer.dll` next to the existing `Data/` + scripts** — doesn't work,
  two independent walls:
  1. `Assembly-CSharp.dll` is welded to the **WP8 runtime**: its assembly refs are Silverlight/WP
     `mscorlib`/`System`/`System.Core` **2.0.5.0** (not desktop `mscorlib 4.0`), plus `WinRTBridge`
     and `WindowsUnityInterface`, plus a WP8-built `UnityEngine.dll` whose methods are `CppInvoke`-
     bound to the ARM engine. A desktop Mono player can't load any of that.
  2. There is no loose desktop `UnityPlayer.dll` for 4.6.9 to obtain: Unity 4.x **statically linked**
     the standalone Windows engine into the game `.exe`; the separate redistributable `UnityPlayer.dll`
     didn't exist until Unity **5.4**. The WP8 build only ships it as a DLL because WinRT demanded a
     component DLL.
  The `Data/` assets themselves are largely portable (format v9, little-endian, DXT textures) — it's
  the code+engine binding and the platform-compiled shaders that are baked per-platform, which is
  exactly why the editor re-bake (§4) is unavoidable.

## 3. Why Unity is *recoverable* where Spark2 was not

The Spark2 study died on **G1 — content decryption** (AES-encrypted, obfuscated `.spd`). Unity has
**no such gate**:

| | AC Pirates (Spark2) | Unity WP (Twins) |
|---|---|---|
| Assets | AES-encrypted, obfuscated `.spd` | Standard `.assets` — open, unencrypted |
| Game logic | Obfuscated ARM Lua | Managed .NET IL, decompilable |
| Engine tooling | none | Unity editor + AssetRipper (mature) |

So a *playable* Unity title is achievable — as a **rebuilt standalone binary**, not a WPR-hosted
app.

## 4. The port recipe (manual, per game, one-time)

> Estimated effort for a small casual title: **1–3 days** for someone comfortable with
> AssetRipper + Unity; **1–2 weeks** learning the tools. The asset recovery is minutes — the time
> goes into stubbing WP8-only SDKs.

1. **Rip** — run AssetRipper (CLI or GUI) over the game's `Data/` folder → reconstructed Unity
   project (scenes, sprites/meshes/audio, and recovered C# from `Assembly-CSharp.dll`).
2. **Open** in a **modern Unity LTS** — *not* 4.6. Modern is mandatory for a shippable Android
   build anyway, and it sidesteps the EOL-4.6 license hassle (use current free Unity Personal).
   Accept the script API-upgrade churn.
3. **Neutralize WP8-only dependencies** (the bulk of the work). For Twins these are:
   - `Microsoft.Xbox` / `GameNetworking` sign-in/achievements/leaderboards → force the existing
     `*Placeholder` backend (the code already has `AchievementPlaceholder` /
     `GameNetworkPlaceholder` alongside the `*XboxLive` ones — it's a switch, not a rewrite).
   - `GoogleAds`, `UnityAdMob`, `OpenAd` → no-op the ad calls.
   - `IAPManager` → stub WP8 Store IAP to "owned/free".
   - `WindowsUnityInterface`, `WinRTBridge`, Geolocation → remove; map touch→mouse for desktop.
   - `DOTween` → reimport the desktop build (same library).
4. **Build** the same project for **Windows Standalone (x64)** and **Android** — the two targets
   WPR consumes. Android is Unity's strongest target; it's essentially free once the project
   compiles.
5. **Register with WPR** — drop the binaries into the game's install folder and write a
   `wpr-port.json` (§6). WPR then launches it from the library like any title.

**Caveats:** the output is a standalone game *outside* WPR's emulation model — it won't inherit
WPR's shims, gamer overlay, tilt input, or achievements unless separately bridged (the
`GameNetworking` placeholder is the future hook). Ripping a commercial title is fine for
personal/preservation use, not redistribution.

## 5. The launcher rail (implemented)

WPR treats a title as a *port* whenever a `wpr-port.json` sits in its install folder — no EF
migration, no installer change. The manifest is authoritative; the stored `ApplicationType` is
irrelevant to dispatch.

- **Model / loader:** `Src/Core/WPR.Loader/UnityPortManifest.cs` (`TryLoad`, `ResolveWindowsExe`).
- **Enum:** `ApplicationType.UnityPort` (documentation/metadata; dispatch is manifest-driven).
- **Desktop:** `Src/Platforms/WPR.Platform.Windows/UnityPortLauncher.cs` — `TryLaunchAsync` probes for the manifest,
  spawns the Windows exe as a child process and awaits exit (mirrors the GameMaker `Runner.exe`
  fast-path). Wired into `MainWindowDesktop` dispatch *before* the Silverlight/XNA branch, and
  into the window-close `RequestExit` chain. **Fully working today** (test with any placeholder
  exe).
- **Android:** `MainActivity.LaunchGame` detects the manifest and routes to `LaunchUnityPort`,
  skipping the WP8 Cecil patching. See §7 for the Unity-as-a-Library status.

## 6. `wpr-port.json` schema

Place at the root of `%LocalAppData%\WPR\AppData\<ProductId>\`. Paths are relative to that folder.

```json
{
  "type": "unity-port",
  "windows": "port/Twins.exe",
  "android": {
    "package": "com.gametroopers.twins",
    "activity": "com.unity3d.player.UnityPlayerActivity",
    "apk": "port/Twins.apk"
  }
}
```

- `type` — must be `"unity-port"`.
- `windows` — desktop Standalone exe (omit if no desktop build).
- `android.activity` — fully-qualified activity to start for a Unity-as-a-Library embed
  (resolved by name; see §7).
- `android.package` — package id; used as the UaaL host package and as the fallback
  launch-by-package target for a separately-installed APK.
- `android.apk` — optional bundled APK for a first-run install prompt (auto-install is future
  work; needs a FileProvider + `REQUEST_INSTALL_PACKAGES`).

## 7. Android: Unity-as-a-Library (chosen model) — status & remaining work

**Decision:** embed the Unity player into WPR.Platform.Android (UaaL), launching the Unity activity
in-app, rather than shipping the port as a separate APK.

**What's in place:** `LaunchUnityPort` resolves `android.activity` **by name**
(`Java.Lang.Class.ForName`) and starts it. Resolving by name means the code compiles *before* any
Unity library is bound, and the embed **lights up automatically** once the AAR is present. Until
then it logs a "library isn't embedded yet" warning and falls back to a launch-by-package intent /
clear error dialog.

**What's still required to make an embed live (per ported game, or once for a shared harness):**

1. Build the Unity project with **Export Project → Android**, producing a `unityLibrary` Gradle
   module / AAR.
2. Bind that AAR into `WPR.Platform.Android` (a .NET-for-Android binding library, or include as an
   `AndroidLibrary`). This is the genuinely uncharted part — UaaL is documented for native
   Gradle/Kotlin hosts, less so for a .NET-for-Android + Avalonia host.
3. Merge Unity's manifest requirements (the `UnityPlayerActivity`, permissions, `<meta-data>`)
   into WPR's `AndroidManifest.xml`.
4. Mind the **single-Unity-instance-per-process** limit: a UaaL player can be shown/hidden but not
   run as two concurrent games. For a library-of-games hub this means load/unload discipline
   around the one Unity activity.

> If UaaL binding proves too costly, the fallback already wired in — a **separate installed APK**
> launched by `android.package` — is the low-risk alternative and needs no Unity/host binding.

## 8. Recommendation

- **In WPR:** Unity WP titles stay at the shell/placeholder until someone does the one-time port;
  then they slot into the library via `wpr-port.json`. Don't attempt engine hosting.
- **The rail is generic:** it's not Twins-specific. GAME TROOPERS shipped several Unity WP titles;
  each becomes launchable by rebuilding + dropping a manifest.
- **First concrete step for Twins:** run AssetRipper over its `Data/` as a cheap go/no-go on how
  cleanly the project reconstructs, before committing to the dependency-stubbing work.

## 9. Spike results — Twins `Data/` (2026-07-11)

Ran a read-only pass over Twins' `Data/` (AssetRipper GUI can't be auto-run here — sandbox blocks
agent-downloaded exes — so this used a small `AssetsTools.NET` probe plus plaintext scans of the
serialized files). **Verdict: GO** — every flagged risk came back benign.

| Signal | Finding | Implication |
|---|---|---|
| Unity version | `4.6.9f1` (a couple assets `4.6.5f1`) | standard, consistent |
| Container format | SerializedFile **format v9**, plain — externals tables read cleanly (mainData → 224 externals) | **not encrypted, not bundled, not obfuscated** |
| Target platform | `26` = **WP8** (a DirectX target) | textures are **DXT**, not iOS PVRTC → transcode cleanly to desktop/Android |
| Scripts | full class list already recovered from `Assembly-CSharp.dll` (un-obfuscated) | managed, decompilable |
| Shaders (the flagged risk) | built-ins + **6 custom**, all trivial **fixed-function `SetTexture { combine }`** ShaderLab (e.g. `Custom/Simple Texture`, `Custom/Solid Color`, `Custom/Separate Alpha Mask`); full source embedded as plaintext and recovers verbatim | **~zero shader risk** — no HLSL/CG programs to port |

**Probe limits (honest):** `AssetsTools.NET 3.0.4` mishandles Unity 4.6 v9 metadata (reported 0
objects; EOF on the 3 largest files), so no object histogram / texture-format counts from this
pass. That's a tool limitation on an old format, **not** a data problem — AssetRipper has
version-specific v9 readers and will enumerate them. Full reconstruction + "does it compile/run"
still needs AssetRipper (GUI) + a Unity editor, i.e. the actual port work.

**Net:** the only remaining effort is the already-enumerated WP8-SDK stubbing (Xbox/ads/IAP), eased
by the `GameNetworking` `Placeholder` backend. Estimate holds — toward the **low end** (~1–2 days
for someone with Unity) given how simple the assets and shaders are.

## 10. Twins port worklist (turnkey — from decompiling `Assembly-CSharp.dll`)

Everything below is derived from the actual decompiled game code, so the Unity-editor session is
"apply this known list," not "hunt for what breaks." **The one step that can't be done headless —
running AssetRipper's GUI + a Unity editor to reconstruct and build — is the human part; the rest
is spelled out here.**

**Recommended toolchain:** AssetRipper 1.3.x (already downloaded) → export Unity project → open in
**Unity 2019.4 LTS**. 2019.4 is the sweet spot: new enough to build modern Windows x64 + Android,
old enough to still accept this project's legacy APIs and **fixed-function ShaderLab** without a
rewrite. (Newer LTS works too but expect more upgrade churn.)

### Step A — reconstruct
1. Launch `AssetRipper.GUI.Free.exe`, **Load Folder** → the game's install `…\<ProductId>\` (or its
   `Data\`), **Export → Unity Project** to a fresh folder.
2. Open that folder in Unity 2019.4 LTS. Let it import; expect shader/API warnings, not blockers.

### Step B — neutralize the WP8-only code (the "hard 20%")
The native surface is small and isolated. `using WinRTBridge;` appears in *every* script but that's
just the auto-injected WP8 tombstoning glue (`SerializeStatePart`/`DeserializeStatePart`) — AssetRipper
strips it; ignore it. The genuinely platform-bound code is:

| Concern | Files | Action |
|---|---|---|
| **Xbox Live / achievements / leaderboards / sign-in** | `GameNetworking/GameNetwork.cs` + the whole `*XboxLive` path (`GameNetworkXboxLive`, `AchievementXboxLive`, `LeaderboardXboxLive`, `XboxProfile`, `XboxUnityManager`, `SignInHandler`, `GameNetServices`) | **One-line flip:** in `GameNetwork.cs` `SetInstance()` change `new GameNetworkXboxLive()` → `new GameNetworkPlaceholder()`. The `*Placeholder` classes are pure managed (no native) and satisfy the whole interface — the Xbox files then go unreferenced. |
| **Ads** | `AdManager`, `Ad`, `OpenAd`, `UnityAdManager`, `UnityAdMob`, `DisplayMoreGamesBannerAlone` | No-op the ad-show / fetch calls (they marshal into `WindowsUnityInterface` native that won't exist). |
| **IAP (remove-ads)** | `IAPManager`, `RemoveAdsButton` | Force "purchased / ads removed = true"; no-op the store call. |
| **Engagement** | `AskForReview`, `RateButton`, `ShareButton`, `Sharing`, `URLButton` | No-op, or map to `Application.OpenURL` / native share as desired. |
| **Analytics** | `CGoogleAnalytics`, `GoogleAnalyticsForApps` | No-op. |
| **Screenshot** | `ScreenshotMaker` | Map to `ScreenCapture` or no-op. |

Mechanically: after import these files won't compile (they reference `WindowsUnityInterface` types
that don't resolve) — that compile-error list *is* your worklist; replace each offending body with the
no-op / forced-value above. Nothing else in the 279 scripts touches platform native.

### Step C — build & register with WPR
1. Build **Windows x64 Standalone** and **Android** from the same project.
2. Drop the binaries into the game's install folder and add `wpr-port.json` (below). WPR then
   launches it from the library via the rail in §5. **Do not add this file until a binary exists** —
   its mere presence flips the title into port mode, so a premature manifest makes launch error out
   instead of showing today's shell.

```json
{
  "type": "unity-port",
  "windows": "port/Twins.exe",
  "android": {
    "package": "com.gametroopers.twins",
    "activity": "com.unity3d.player.UnityPlayerActivity"
  }
}
```

3. (Optional, later) Bridge achievements back to WPR via the `GameNetworkPlaceholder` hooks — see §7 /
   the `Placeholder` backend. Not needed for a playable build.

---

## 11. Track 4 spike: Unity and Spark2 on the ARM CPU (2026-10-12)

`Plans/RUNTIME-CONVERGENCE.md` §5 asked whether dynarmic (`WPR.Engine.Wp8Native`) reopens §2's
"ARM code cannot run" verdict. It set three questions, to be answered in order. Nothing was built;
the two probe tools were throwaway and lived in the gitignored `scratchpad/track4/`.

**Verdict: Spark2 (AC Pirates) GO. Unity NO-GO for an in-process bridge, so the port rail (§4-§6)
stays the supported path.** Question 1 is a yes for both engines. Unity fails on the size and memory
model of questions 2 and 3, not on the CPU.

Measured, extracted from the library XAPs: Twins Minigame (Unity 4.6), Bad Piggies (4.6), Temple
Run 2 (4.3) and Assassin's Creed Pirates (Spark2).

### Q1: does the loader map the native engine and run its DllMain? Yes, for all four

The probe built an `ArmEmulator` over the component DLL, then called `WinRt.StartComponent` with an
empty `XamlShell`. That runs the real host path: package DLLs' DllMains, the component's DllMain,
then `DllGetActivationFactory`. Each was run with and without `WPR_WP8_TRAP_IMPORTS=1`.

| component | package DLLs mapped | DllMain | activation factory | instructions |
| --- | --- | --- | --- | --- |
| Twins `UnityPlayer.dll` (8.3 MB) | none (system imports only) | TRUE | `UnityPlayer.UnityApp` S_OK | 1.28 M |
| Bad Piggies `UnityPlayer.dll` | none | TRUE | S_OK | 1.28 M |
| Temple Run 2 `UnityPlayer.dll` | none | TRUE | S_OK | 1.28 M |
| AC Pirates `SparkApp.dll` | 8: Lua, LuaBindTools2 (+Helpers), SparkSystem, OMath, SparkUtils, SparkResourceManager, SparkRemote. 13,904 relocations, every DllMain TRUE | TRUE | `SparkApp.SparkAppBridge` S_OK | 12 k |

- **No unimplemented import was hit, and nothing trapped.** That matches the census, which shows
  0 unimplemented imports for every Unity title and for AC Pirates.
- **`ActivateInstance` fails on both factories, and that is expected.** `UnityApp` is static-only:
  its methods are on `__IUnityAppStatics`, reached by QI on the factory (XamlInterop's
  `_staticInterfaces`). `SparkAppBridge` is built through `__ISparkAppBridgeFactory.CreateInstance`.
- **Not exercised:** Spark2 loading its 25 Lua modules under `Spark2Projects\` at run time with
  `LoadPackagedLibrary`, and anything after activation.

### Q2: the managed->native surface

Counted with Cecil. The pass looked for every call, callvirt, newobj, ldftn or ldsfld from a package
managed assembly into a type that a package `.winmd` defines, and separately for Unity's own engine
binding.

**The shell is small in both engines:**

| title | shell assembly -> native winmd |
| --- | --- |
| Twins | `Twins.dll`: 16 `UnityApp` statics plus `new UnityPlayer.Action`. The statics are SetBridge, GetBackgroundContentProvider, GetManipulationHandler, Set{Native,Render}Resolution, SetOrientation, SetLoadedCallback, Obscure/UnObscure, Deactivate, BackButtonPressed, Quit and three location calls. Its other 23 members are Microsoft.Xbox. |
| Bad Piggies | the same 16 `UnityApp` statics, plus `BeginInvoke` |
| AC Pirates | `SparkApplicationXaml.dll`, **13 members at 13 sites**: the `SparkAppBridge` ctor, CreateContentProvider, Native/RenderResolution, WindowBounds, SetDeviceData, OnOrientationChanged, OnBackButtonPressed, OnObscured/OnUnObscured, the `LuaMobileSDKBridge` ctor and `SocialConnection_OpenUrl` |

**Unity's real managed->native surface is its engine binding, not the shell.** `UnityEngine.dll`
reaches the player through function-pointer tables that native fills in at start-up. It does **not**
use CppInvoke: `CppInvokeAttribute` is defined in Twins' UnityEngine.dll and applied to nothing.

| layout | managed -> native |
| --- | --- |
| Unity 4.5/4.6 (Twins, Bad Piggies) | `UnityEngine.Internal.$FuncPtrs` holds **2,917 / 2,908** native function pointers. Native fills them through `$FuncPtrs::$Invoke0` -> `Initialize(IntPtr)`. They are called through `$Calli::InvokeN`: **179 distinct calli signatures**, one call site per function. |
| Unity 4.3 (Temple Run 2) | `UnityEngineProxy.InternalCalls` has **2,726** entry points. Each is `Marshal.GetDelegateForFunctionPointer` over a native table (`funcPointers` + offset). |

In Twins, **749 of the 2,917 callers hand native an address of managed memory**: a pinned local, or
an address followed by `conv.i`, as in `Transform.INTERNAL_get_position(ref Vector3)`. 172 pass
strings, and 189 pass or return arrays. The address heuristic is an upper bound.

### Q3: the native->managed surface

| | Twins | Bad Piggies | Temple Run 2 | AC Pirates |
| --- | --- | --- | --- | --- |
| Managed classes implementing package-winmd interfaces (native calls their vtables) | 14 `WinRTBridge.*` classes (IBridge, IMarshalling 37, IWP8Utility 15, IGCHandles, IScriptingPinnedArray, …), **134 methods** | 14, **129 methods** | 19, **174 methods** | 2: `CSharpFunctions` (15: microphone, MediaPlayer, NavigateToUrl, ShowPopup, IsInternetAvailable) and `LuaMobileSDK.ICallback` (7). **22 methods** |
| `$Invoke` thunks, `int64 $InvokeN(int64 gcHandle, int64* args)`, given to native by `ldftn` from `$UnityTypeNNN` constructors | **9,146**: UnityEngine 6,129, UnityEngine.UI 1,170, Assembly-CSharp 1,120, DOTween 691, other 36 | **13,925** | none; 4.3 uses reflection-driven `IMethodTools` (15 methods) instead | none |
| Winmd delegates constructed in managed code | `UnityPlayer.Action` (the loaded callback) | the same | none | none |

`WinRTBridge.TypeInformationGatherer` also writes managed method pointers into
`BridgeInterface.DotNetMethod.methodPtr` and `DotNetField.getter/setterMethodPtr`, for native to
call.

### Why Unity is NO-GO

The counts alone are not the blocker. 179 calli shapes and one thunk signature are mechanical for a
Cecil pass. Three things are.

1. **Unity WP8 assumes managed and native share one 32-bit address space.** The calli stubs pass
   managed addresses as `conv.i; conv.i4`, which truncates them to 32 bits. That is already wrong
   on any 64-bit host, before an emulator is involved, and under dynarmic the guest cannot see host
   memory at all. So these all need copy-in/copy-out:
   - the ~750 address-passing sites;
   - the string and array sites;
   - `IScriptingPinnedArray.get_Ptr`, through which native reads and writes managed array storage
     directly.

   That copying needs per-site knowledge of sizes. It breaks outright wherever native holds a
   pointer across calls.
2. **The thunks read guest memory from managed code.** Every `$Invoke` reads its arguments through
   `int64* args`, which points into guest memory. `IArmCpu` only offers `MemRead`/`MemWrite`
   copies, with no host view of guest memory. So either `wprcpu` exports one (a native change), or
   9-14 k thunk bodies get rewritten.
3. **The trap-slot budget is nearly gone.** Each managed function native can call needs its own
   guest address. Bad Piggies publishes 13,925. The trap page has 16,384 slots, and return traps
   and import thunks draw on the same page.

**Threading and per-frame cost add to that.** Unity runs its own worker threads (loading, audio),
and its whole frame loop runs over the bridge, so 3 k entry points pay the per-call cost every
frame. Altogether this is weeks of work. For any title that matters, §4's rebuild gives a better
result: native speed and a modern Android build. **Revisit only if** a shared-memory guest heap
(managed buffers allocated inside guest space) gets built for some other reason.

### Why Spark2 is GO

**Spark2 has the same shape as the component titles WPR already hosts (Alex, MC4).** It is a C++/CX
`SparkAppBridge` with a content provider, 13 shell calls, and one managed callback interface of 15
methods.

**Running the game's own native code also removes the blocker in §3.** The `.spd` decryption that
killed the Spark2 study happens inside that code, so WPR never has to reimplement it.

Next steps, in order:

1. Add a `XamlShells` entry for `SparkApp.dll`, plus `CreateInstance` activation through
   `__ISparkAppBridgeFactory`. Today the shell only knows `ActivateInstance` and `InstanceFrom`.
2. Add host COM objects for `SparkApp.CSharpFunctions` and `LuaMobileSDK.ICallback`.
   `CSharpFunctions` has 15 methods, mostly "no microphone", the media player state, and the popup.
3. Check the run-time `LoadPackagedLibrary` of `Spark2Projects\**\Lua*.dll`, which loads from
   subfolders. Then check Bink (`bink2winphone_arm.dll`) and OpenAL (`wrap_oal.dll`).
   **Done 2026-10-12 (branch `spark2-loader`).** `Lua.dll`'s `package.loadlib` converts each
   `package.cpath` candidate with `mbstowcs_s` and calls `LoadPackagedLibrary(path, 0)` verbatim,
   then `GetProcAddress(lib, "luaopen_x")`. SparkApp prepends `<projects dir>\?.dll` and
   `<projects dir>\__Spark2Lib\?.dll` to the default `.\?.dll;!\?.dll;!\loadall.dll`, and
   `SparkSystem::GetWorkingDirectoryPath` answers "" on WP8, so the paths are package-relative.
   All 24 ARM Lua modules plus `bink2winphone_arm.dll` map, bind and return TRUE from DllMain
   (`Wp8ModuleProbe --load`), every import implemented or bound, all 26 `luaopen_*` resolved,
   nothing trapped under `WPR_WP8_TRAP_IMPORTS=1`. **`wrap_oal.dll`, `OpenAL32.dll` and
   `_Bink\binkw32.dll` are x86 (machine 0x14C) desktop leftovers**: nothing ARM imports them and
   they fail with ERROR_BAD_EXE_FORMAT, as on a device. Spark2's sound is
   `LuaGeeaSoundEngine.dll` over XAudio2_8 (X3DAudio ordinals 5/6 included). Two loader fixes:
   `.` / empty path segments were not folded (`.\x.dll` failed), and a path was matched to a
   loaded module by base name, which would have handed `Spark2Projects\_MobileSDK\LuaMobileSDK.dll`
   the root `LuaMobileSDK.dll` WinRT component (or the reverse). Whoever activates
   `LuaMobileSDK.LuaMobileSDKBridge` must load the ROOT file by path for the same reason.
   Contract suite: `PackagedLibrarySuite`.
4. **Done 2026-10-12 (branch `acp-services`): the page's other component, saving, audio routing.**
   - `LuaMobileSDKBridge` is activated where the managed page does it (after the resolutions,
     before `CreateContentProvider`), with the host's `ICallback`. It is generic: a
     `XamlShellStep` with `Class` set goes to any runtime class WMAppManifest.xml registers
     (`WinRtRuntime.PackageClasses`); `ActivatePackageFactory` loads the DLL by its package path
     (`.\LuaMobileSDK.dll`). Two fixes were needed on the way: vccorlib's `GetActivationFactory`
     walked only the shell component's creator map (it now uses the module owning the
     `ModuleBase*`; the forward is a tail call, so the return address is useless), and a
     host-driven dynamic load had its continuation overwritten when the loader restored r12.
   - **Saving.** The mobile SDK's SQLite (`msdk` in the local folder) goes through its own VFS
     (`sqlite_vfs_win8.cpp` in the Lua module) over the CRT descriptor API, and its xSync is
     `FlushFileBuffers(_get_osfhandle(fd))`. `_get_osfhandle` answered -1 for a descriptor from
     `_open`, so every journal sync failed and every write transaction rolled back ("Error while
     creating UserPreferences table", "no such table"). It now mints a handle owned by the
     descriptor. The package's `msdk` is the SDK's own encrypted database, copied on first run;
     the game creates its tables only then, so a store written by an older build (copied, tables
     never created) stays without them until the game's data is cleared. `SqliteSuite` runs the
     title's real `sqlite3.dll` in the guest over the Win32 file API.
   - `Windows.Phone.Media.Devices.AudioRoutingManager` (asked for by `LuaDevice.dll`) is
     implemented: loudspeaker, Earpiece|Speakerphone available, `AudioEndpointChanged` raised
     through the deferred queue. Not yet reached in a 90 s run: the level load dies first in the
     GPU replay (`Could not compile input layout` on a BLENDINDICES skinned draw, then an access
     violation in `FNA3D_ApplyVertexBufferBindings`).

**To reproduce:**

1. Extract each XAP's `*.dll` and `*.winmd`.
2. **Q1:** build an `ArmEmulator` over the component and call
   `WinRt.StartComponent(image, WinmdReader.Load(winmds), new XamlShell(dll, classId, [], []))`.
   `ComponentLog` holds the lines quoted above.
3. **Q2/Q3:** run a Cecil pass over the package that collects:
   - member references whose scope is a package `.winmd`;
   - implementations of winmd interfaces;
   - `UnityEngine.Internal.$FuncPtrs` and `$Calli`;
   - `int64 $Invoke*(int64, int64*)` methods and their `ldftn` registrations.
