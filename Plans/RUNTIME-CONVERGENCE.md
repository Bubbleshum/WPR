# Runtime convergence: one stub policy, one renderer, one input path

**Status (2026-10-10):** Proposed. Nothing here is started.
**Written against:** `f58fa86`.
**Prompted by:** Angry Birds Go! (WP8 native) dying on its first frame with `null call to 0x00000000`,
and the question of how much of WPR's runtime is per-game and how much could be shared.

**Verdict:** The ARM CPU core is generic and is not the problem. The fragility is in the WP8
stub layer, which answers unknown calls with 0 and carries on. Graphics and input are already
mostly converged: XNA, mixed-mode Silverlight and WP8 native all draw through the XNA
`GraphicsDevice` and read input from `TouchPanel`. Pure Silverlight on Avalonia is the one
holdout. The WP8 runtime also keeps its own copies of a few things the XNA side already does.

The work splits into five tracks. Track 1 comes first because it makes every other WP8
failure readable. The others are independent.

| track | what | size | blocks |
| --- | --- | --- | --- |
| 1 | Make the WP8 stub layer fail loudly and implement APIs by contract | medium | nothing |
| 2 | Finish the Silverlight → XNA host convergence | large | the `IBackgroundRenderer` decision |
| 3 | Fold the WP8 runtime's duplicates into the shared services | small, each item separate | nothing |
| 4 | Spike: host Unity / Spark2 titles on the ARM CPU | unknown | track 1 |
| 5 | Bring the docs up to date | small | nothing |

## 1. The measurement that motivates this

Angry Birds Go! (`WPR-C32A763J` in the hub) on Android, logging run, 2026-10-10:

```
[wpr-wp8] guest stopped after 0 frame(s): CPU exception 12 at 0x00000000
[wpr-wp8]    null call to 0x00000000 from 0x005BC6CB
[wpr-wp8]    thread 1 'main': blocked on std::thread start
[wpr-wp8]    thread 2..6 'std::thread': blocked on condition ... resume 0x005C20FB
[wpr-wp8]    thread 7 'std::thread': running
[wpr-wp8]    defaulted:        1  InterlockedPushEntrySList
[wpr-wp8]    defaulted:        1  InterlockedFlushSList
[wpr-wp8]    defaulted:        4  CreateFileMappingFromApp
[wpr-wp8]    defaulted:        4  _stat64
[wpr-wp8]    defaulted:        2  ?__abi_WinRTraiseNotImplementedException@@YAXXZ
[wpr-wp8]    last call: api-ms-win-core-interlocked-l1-2-0.dll!InterlockedFlushSList
```

"Defaulted" means the import reached no handler and was answered with 0
(`HostStubs.DefaultedCalls`). Five worker threads parked at one address on condition variables
is a job scheduler. The last call before the crash is the SList flush, and both SList entry
points are defaulted. **Hypothesis, not yet proven:** the game pushes a job that is never linked,
a worker gets an empty or garbage entry back, and calls through its null function pointer.

The wider point is the policy, not this one game. Silent defaulting means a title works only if
it happens not to depend on a stubbed call's result. Two `__abi_WinRTraiseNotImplementedException`
calls were swallowed here too: the game asked to abort a path and execution carried on.

## 2. Track 1: the WP8 stub layer

### 1a. Trap instead of default

`HostStubs` answers an unhandled import with 0 and records it in `DefaultedCalls`. Change that
to stop the guest with the import's name and caller address, and keep an explicit allowlist of
imports that are genuinely safe to answer with 0.

Allowlist candidates from this log: `_onexit`, `__crtSetUnhandledExceptionFilter`,
`IsProcessorFeaturePresent`. Each entry gets a one-line reason, like the patcher tables do.

`__abi_WinRTraiseNotImplementedException` must never be on the list. It is the game throwing;
raise it as a guest C++/CX exception through the existing `CxxExceptionModel`.

**Exit:** `DefaultedCalls` is always empty for non-allowlisted imports. A WP8 crash report names
the first unimplemented import instead of a null call several frames later.

> **Revised 2026-10-11: trapping is a diagnostic, never the default.** The goal is every game
> running in one shared runtime, so an unknown game must not be blocked up front for using
> something not yet implemented: it runs until it actually fails, and the failure reaches WPR Hub.
> Built as:
>
> - **Default (what players get):** an unimplemented import is answered with 0 and recorded in
>   `DefaultedCalls` (in the order first met); an unimplemented WinRT member is answered from its
>   signature, or by the old shape-guessing, and recorded in `DefaultedSlots`.
> - **`WPR_WP8_TRAP_IMPORTS=1`:** stop at the first unimplemented import or unanswerable member, by
>   name. The regression harness uses it (`wp8-regression.ps1 -Trap`) to find what to implement
>   next; nothing else sets it. (Trap-by-default was built first and stopped all 11 installed
>   titles at boot - which is why this was revised.)
> - **Every guest that stops on its own is reported.** `Wp8NativeGame.Failure` carries the stop
>   reason, frames presented and the post-mortem (summary line with `defaulted=[…]`, `raised=[…]`,
>   `defaultedslots=[…]`, registers, threads, last calls); `ApplicationLaunch.ReportGuestFailure`
>   queues it as a hub crash report (`RuntimePath = other`, `ExceptionType = "WP8 guest stopped"`,
>   the stop reason as the top "frame" so the hub groups one fault as one crash). A player's
>   failure therefore arrives with the list of everything that was defaulted on the way to it.
>
> What stays: the allowlist (it keeps the defaulted list honest - only real lies are on it),
> delivering `__abi_WinRTraise*` as real guest exceptions, and every real implementation from 1c
> and the to-do list below. Those make games get further; none of them stop one.

### 1b. A regression run across the library

Before changing anything else, run every installed WP8 native title headless for a fixed
instruction budget (the `Research/Wp8Native` probe already does single titles) and record:
frames presented, the first trap, and its import name. Keep the table in this file or next to
it, and diff it after every change in this track.

Expect the table to look *worse* after 1a. Titles that were limping past bad stubs will now
stop at a named trap. That is the point: the table becomes a ranked to-do list, and the imports
that block the most titles get implemented first.

### 1b results (2026-10-11)

Measured against `f58fa866` plus the uncommitted 1a work (`HostStubs` trap policy,
`PlatformRaise`, `ICoreApplication` lifecycle slots, the per-run `summary:` line). Desktop,
dynarmic, 45 s per title from clean saves, `scratchpad/gamerun/wp8-regression.ps1`; raw output in
`scratchpad/gamerun/wp8-out/{head,legacy,trap}/`. `head` is the code before any of this, `legacy`
is the new code with `WPR_WP8_DEFAULT_IMPORTS=1`, `trap` is the new default.

| title | head | legacy | trap | trap import (caller) | note |
| --- | ---: | ---: | ---: | --- | --- |
| `AngryBirdsClassic.exe` (Angry Birds) | 2216 | 2442 | 0 | `vccorlib110!?InitializeData@Details@Platform@@YAJH@Z` (0x0062EFD4) | |
| `AngryBirdsRio.exe` (Angry Birds Rio) | 2491 | 2484 | 0 | `vccorlib110!?InitializeData@…` (0x0057F764) | 376 `_Orphan_all` calls |
| `AngryBirdsSeasonsFull.exe` (Seasons) | 1762 | 1696 | 0 | `vccorlib110!?InitializeData@…` (0x005DC3EC) | |
| `AngryBirdsSpace.exe` (Space) | 2428 | 2450 | 0 | `vccorlib110!?InitializeData@…` (0x005AB438) | |
| `AngryBirdsStarWars.exe` (Star Wars) | 2273 | 2463 | 0 | `vccorlib110!?InitializeData@…` (0x00594034) | |
| `AngryBirdsStarWarsII.exe` (Star Wars II) | 1967 | 2383 | 0 | `vccorlib110!?InitializeData@…` (0x00631D04) | 16 `__abi_WinRTraiseObjectDisposedException` still **defaulted**, see below |
| `Purple.exe` (Angry Birds Stella) | 2143 | 2356 | 0 | `vccorlib110!?InitializeData@…` (0x0060BA94) | |
| `AlexXamlComponent.dll` (Amazing Alex) | 2260 | 2335 | 0 | `MSVCR110!_lock` (0x1018E9AA) | `_lock`/`__dllonexit`/`_unlock` x828 each, from the DLL CRT's `atexit` |
| `MC4HybridComponent.dll` (Modern Combat 4) | 1762 | 2292 | 0 | `MSVCR110!_lock` (0x105631C6) | 1 `ObjectDisposedException` still defaulted |
| `WindowsPhone.exe` (Terraria) | 0 | 0 | 0 | `vccorlib110!?InitializeData@…` (0x0083C5CC) | pre-existing: `CPU exception 12 at 0x00000000` on head and legacy, see below |
| `WP8Comp.dll` (GTA: San Andreas) | 0 | 0 | 0 | none | pre-existing: no Direct3D/XAML page description; never reaches the CPU |

**Legacy against head: no regression.** Every title is within ±25 % of head or above it (MC4
+30 %, Star Wars II +21 %; the rest −4 % to +10 %). `raised=[]` on every legacy run: with
`ICoreApplication`'s Suspending/Resuming/get_Id implemented, the Angry Birds titles no longer
raise `NotImplementedException` at boot, so nothing newly thrown into a guest changed its path.
(`?ReCreateFromException@Details@Platform@@`, which the earlier runs defaulted once per Angry
Birds title as the tail of that throw, is gone from every list.)

**A gap in 1a: `ObjectDisposedException` is not in `PlatformRaise.KnownHResults`.** Every title
imports `?__abi_WinRTraiseObjectDisposedException@@YAXXZ`, `TryParse` does not recognise it, so it
falls through to the default (legacy: answered 0, 16 times in Star Wars II, once in MC4) or the
trap (default policy). It must be raised like the others: `RO_E_CLOSED` = `0x80000013`. In Star
Wars II the sites are C++/CX ABI method bodies of the shape `ldrb r3,[r0,#0x1c]; cbnz → raise`
(e.g. 0x004408A4 → 0x004408D2, 0x004EBB66), i.e. a call on an object whose disposed flag is set,
and its inline HRESULT→exception switch at 0x004012EE maps `0x80000013` to the same import. The
summary does not say which of the ~40 sites fired; recording the caller in `DefaultedCalls` would.

**A second gap: shaped constructors are still silent.** `??0…` imports with no handler go to
`ConstructShapedObject` and never reach the trap, e.g.
`??0scoped_lock@critical_section@Concurrency@@QAA@AAV12@@Z` (Alex, MC4), whose real job is to
lock the critical section and store it at `this+0`. 1e removes these; until then the trap policy
does not cover them.

**Terraria (pre-existing).** Head and legacy die at `null call to 0x00000000 from 0x00463F51`: a
virtual `delete` of a heap `std::ifstream` (96 bytes) held at `+0x18` of an object naming
`…enLocalization.txt`, whose `basic_ios` subobject at `+0x10` has a null vptr. Its stream path
runs entirely through defaulted imports: `std::_Fiopen` x37 (every open "fails"), `tellg` x38
(an `fpos` returned through a hidden pointer that 0 never writes), `seekg` x38 and `read` x1 (both
return a null `istream&`). Trap mode stops it at `InitializeData` first, like the Angry Birds
titles; with the vccorlib start-up group below implemented, its trap order predicts
`OutputDebugStringW`, `_Container_base0::_Orphan_all`, then `?_Fiopen@std@@` — the first named
trap on the path that actually kills it.

### 1b to-do (ranked)

Every title's trap import plus every name in its legacy `defaulted=[…]` list, ranked by titles
blocked. Counts are legacy calls in 45 s. **SAFE-0** means 0 is exactly the documented answer and
can go on `SafeToDefault` with the reason given; **IMPLEMENT** means it cannot.

| # | import | titles (legacy calls) | contract | verdict |
| --- | --- | --- | --- | --- |
| 1 | `?InitializeData@Details@Platform@@YAJH@Z` (vccorlib) | 10: all but GTA, x1 each; **the trap for 8** | `HRESULT InitializeData(int)`: initialises vccorlib's per-module data; no out-parameters | **SAFE-0**: "S_OK; it initialises vccorlib's own module state, and vccorlib is the host here, so there is nothing to initialise". A named `Return(S_OK)` handler is equally good. |
| 2 | `?_Orphan_all@_Container_base0@std@@QAAXXZ` | 10 (Rio 376, Seasons 69, others 2–12) | `void`; `_Container_base0` is the `_ITERATOR_DEBUG_LEVEL=0` base, and every member of it is an empty function in MSVCP110 | **SAFE-0**: "_Container_base0 is the no-debug-iterator base; _Orphan_all is an empty function" |
| 3 | `??1scoped_lock@critical_section@Concurrency@@QAA@XZ` | 9 (AB titles 21–255, Alex 10, MC4 10) | `void`; releases the `critical_section` stored at `this+0` | **IMPLEMENT** with the class: today `?lock`/`?unlock` are host no-ops and the scoped_lock ctor is shaped, so 0 matches the current model, not the contract. Do `critical_section` (ctor/lock/try_lock/unlock/dtor) and `scoped_lock` (ctor stores `this+0` and locks, dtor unlocks) together over `ThreadLibrary`'s owner counts. Guest threads switch inside blocking imports, so a holder can be switched out. |
| 4 | `setjmp` | 8 (MC4 40, Alex 30, Rio 26, Space 18, Seasons 12, Classic 9, SWII 8, Stella 1) | `int setjmp(jmp_buf)`: writes the callee-saved state into the buffer, returns 0; returns again with `longjmp`'s value | **IMPLEMENT**, with `longjmp` (imported by all 11 titles, handled nowhere; not reached in 45 s). ARM `_JUMP_BUFFER`: Frame, r4–r11, Sp, Pc, Fpscr, d8–d15. setjmp stores r4–r11, sp, lr, FPSCR, d8–d15 and returns 0; longjmp restores them and returns `val ? val : 1` at the saved lr. 0 leaves the buffer unwritten. Likely libpng/libjpeg error recovery. |
| 5 | `?uncaught_exception@std@@YA_NXZ` | 8 (Classic 252, SWII 244, SW 221, MC4 215, Rio 214, Stella 211, Space 12, Seasons 8) | `bool`: true while a thrown exception has not reached its handler | **IMPLEMENT** (one line): `Return(unwinding ? 1 : 0)`, true from `ThrowIntoImage` until the catch funclet is entered. 0 is right for every call outside unwinding; these are ostream sentry destructors. |
| 6 | `_get_osfhandle` | 8 (Classic 162, Rio 141, SW 123, SWII 121, Stella 119, Seasons 15, Space 15, Alex 5) | `intptr_t _get_osfhandle(int fd)`: the OS handle, or `-1` with errno `EBADF` | **IMPLEMENT**: look the descriptor up in `FileLibrary`'s table (`_fileno`/`_close` already use it) and return its handle, else `-1` and errno through the existing `_errno` slot. 0 is neither a handle nor the error value. |
| 7 | `__getmainargs` | 8: the 7 Rovio exes + Terraria, x1 | `int __getmainargs(int* argc, char*** argv, char*** env, int wildcard, _startupinfo*)`, 0 on success | **IMPLEMENT**: three out-parameters. Write `argc = 1`, `argv = {"<exe>", NULL}`, `env = {NULL}` in guest memory, return 0. |
| 8 | `?GetCmdArguments@Details@Platform@@YAPAPA_WPAH@Z` | 8, x1 | `wchar_t** GetCmdArguments(int* argc)`: the argument vector for `main(Array<String^>^)` | **IMPLEMENT**: out-parameter. Write `*argc` and return a guest `wchar_t*` array (exe name, NULL). 0 leaves `argc` unwritten and hands back a null vector. |
| 9 | `?GetIBoxArrayVtable@Details@Platform@@YAPAXPAX@Z` | 8, x1 | `void* GetIBoxArrayVtable(void*)`: vccorlib's `IBoxArray` vtable for boxing `Platform::Array` | **IMPLEMENT**: return a guest vtable built like the other host COM objects in `WinRtRuntime` (`CreateObject`), with IUnknown/IInspectable slots and `get_Value`. A null vtable is harmless only until the image boxes an array. |
| 10 | `??1critical_section@Concurrency@@QAA@XZ` | 7 Rovio exes (117–126; Seasons 6, Space 9) | `void`; destroys the lock | **IMPLEMENT** as part of #3. 0 is correct only while the class has no host state, which #3 changes. |
| 11 | `_lock` / `_unlock` | 2: Alex 828, MC4 168; **the trap for both** | `void _lock(int locknum)` / `_unlock`: acquire/release CRT lock n (here `_EXIT_LOCK1` around `__dllonexit`) | **IMPLEMENT** (one line each): route to the same model as `EnterCriticalSection`/`LeaveCriticalSection` in `SyncLibrary`, so CRT locks change when that model does. |
| 12 | `__dllonexit` | 2: Alex 828, MC4 168 | `_onexit_t __dllonexit(_onexit_t f, _PVFV** begin, _PVFV** end)`: append `f` to a DLL's private table, growing it through the in/out pointers; returns `f`, or NULL on failure | **IMPLEMENT**: `Return(Arg(0))` (never run, same reasoning as `_onexit`). 0 is the failure answer. |
| 13 | `DisableThreadLibraryCalls` | 2: Alex, MC4, x1 | `BOOL`; nonzero on success | **IMPLEMENT**: `Return(1)`. 0 is FALSE. |
| 14 | `?_Swap_all@_Container_base0@std@@QAAXAAU12@@Z` | 2: Alex 2, MC4 1 | `void`, empty in the IDL=0 base | **SAFE-0**: same reason as #2. (`_Container_base12::_Swap_all`, seen in an earlier Alex run, is *not* empty: it swaps proxy pointers. IMPLEMENT if it comes back.) |
| 15 | `?__abi_WinRTraiseObjectDisposedException@@YAXXZ` | 2: SWII 16, MC4 1 | throws `Platform::ObjectDisposedException` | **IMPLEMENT**: add `["ObjectDisposedException"] = 0x80000013` to `PlatformRaise.KnownHResults`. Never SAFE-0. |
| 16 | `GetCurrentThread` | MC4, x1 | `HANDLE`: the pseudo-handle `(HANDLE)-2` | **IMPLEMENT**: `Return(0xFFFFFFFE)`; 0 is NULL. |
| 17 | `CoInitializeEx` | MC4, x1 | `HRESULT`: S_OK first time on a thread, S_FALSE after | **SAFE-0** for a first call per thread: "S_OK; there are no apartments here and nothing depends on one". Strictly a repeat answers S_FALSE; count per thread if a title calls it twice. |
| 18 | `remove` | MC4, x1 | `int remove(const char*)`: 0 if deleted, -1 + errno | **IMPLEMENT**: share `DeleteFileW`'s body (writable sandbox only) on the narrow path; else -1 / `ENOENT`. 0 claims a delete that did not happen. |
| 19 | `??5?$basic_istream@…@QAAAAV01@P6AAAVios_base@1@AAV21@@Z@Z` | MC4, x1 | `istream& operator>>(ios_base& (*pf)(ios_base&))`: calls `pf` on the `ios_base` subobject, returns `*this` | **IMPLEMENT** in `StreamLibrary`: call `pf(this + vbtable[1])` in the guest, `Return(this)`. 0 is a null reference. |
| 20 | `OutputDebugStringW` | Terraria, x4 | `void` | **SAFE-0**: "void; the string goes to a debugger, and none is attached". Logging it would be a bonus. |
| 21 | `?_Fiopen@std@@YAPAU_iobuf@@PBDHH@Z` | Terraria, x37 | `FILE* _Fiopen(const char*, ios_base::openmode, int prot)`: NULL on failure | **IMPLEMENT**: map `openmode` to an fopen mode string (in → `r`, out\|trunc → `w`, app → `a`, binary → `b`) and call `FileLibrary.OpenStdio`. |
| 22 | `?tellg@?$basic_istream@…` | Terraria, x38 | `fpos<int> tellg()`, returned through the hidden pointer in r0 (this in r1) | **IMPLEMENT**: `fail() ? -1 : rdbuf()->pubseekoff(0, cur, in)`, written as a full `fpos` to the hidden buffer. 0 leaves it unwritten. |
| 23 | `?seekg@?$basic_istream@…@_JH@Z` | Terraria, x38 | `istream& seekg(off_type (64-bit), seekdir)` | **IMPLEMENT** through `rdbuf()->pubseekoff`, set failbit on -1, `Return(this)`. |
| 24 | `?read@?$basic_istream@…@PAD_J@Z` | Terraria, x1 | `istream& read(char*, streamsize (64-bit))` | **IMPLEMENT**: `sgetn`, store the count in `_Chcount` (+8), set eof\|fail on a short read, `Return(this)`. |

Implementing 1, 7, 8, 9 (the vccorlib start-up group) moves eight titles off their first trap;
11 and 12 move Alex and MC4. After that the order above is roughly the order the trap run will hit
next.

One existing allowlist entry does not meet the bar this table holds others to: `_onexit` returns
`func` on success and NULL on *failure*, so 0 is the failure answer. Its reason is about callers
(static-init `atexit` ignores the result), not the contract; `Return(Arg(0))` would be exactly
correct and costs the same.

### 1c. Implement the core primitives to their documented contract

Not to what one game needed. These are finite and well specified:

| API | contract notes |
| --- | --- |
| `InitializeSListHead`, `InterlockedPushEntrySList`, `InterlockedPopEntrySList`, `InterlockedFlushSList`, `QueryDepthSList` | ARM32 `SLIST_HEADER` is 8 bytes: next pointer, then depth and sequence. Push returns the previous head. Guest scheduling is ours, so one host lock around a plain singly linked list is enough. |
| `CreateFileMappingFromApp`, `MapViewOfFileFromApp`, `UnmapViewOfFile` | Read-only mappings of files in the install folder cover asset archives. Map by copying into guest memory; writes back are not needed until a title proves otherwise. |
| `_stat64` | Fill the struct from the host file, through the same path resolution `FileLibrary` uses. Return -1 and set errno for a missing file; a 0 with an unfilled struct reports every file as existing and empty. |

**Exit:** Angry Birds Go! presents at least one frame, or traps on a named import that is not
one of these. Either outcome confirms or refutes the SList hypothesis in §1.

### 1d. Generate WinRT vtables from .winmd

`WinmdReader.cs` already exists. Use it to build each interface's slot table with real names
and signatures, so the log says `ICoreWindow::add_KeyDown` instead of `ICoreWindow::slot34`,
and an unimplemented slot traps by name like an import does in 1a. Hand-written slot constants
(`SlotAddPointerPressed` and friends in `WinRtRuntime`) become lookups.

Do this after 1a and 1b, so the regression table shows it changed nothing.

### 1e. Retire shape-guessing

`ConstructShapedObject` gives a zeroed object a synthetic vtable, and `LooksLikeVtable` decides
whether an existing first word is a real vptr by probing memory. In this log it probed
`0x42413938`, which is ASCII string data, and threw `CpuMemoryException` (caught, but logged as a
first-chance exception and expensive).

- **Now:** make `LooksLikeVtable` check the address is mapped before reading, instead of relying
  on the exception. Cheap and independent.
- **After 1d:** when activation goes through generated class info, the stub knows the object's
  layout and `ConstructShapedObject` has nothing left to do. Remove it then, not before; titles
  may currently depend on it.

### 1f. Host noise worth clearing

`Wp8NativeGame.OnExiting` and `Dispose` both release `_tick` (`SemaphoreSlim(0, 2)`) on shutdown
and the second release throws `SemaphoreFullException`. It is caught, so it is harmless, but it
puts two stack traces in every crash report. Release once, or guard on `CurrentCount`.

## 3. Track 2: one renderer and one input path

### Where things stand

| runtime | graphics | input |
| --- | --- | --- |
| XNA | `GraphicsDevice` → `IGraphicsBackend` → FNA3D | SDL → `IInputBackend` → `TouchPanel` |
| mixed-mode Silverlight | `SoftwareVisualRasteriser` → `Texture2D` on the same device | `TouchPanel` → `MixedModeGame.PumpSilverlightTouch` → routed events |
| WP8 native | `Wp8GpuReplay` replays the guest's D3D11 frame on the same device | `TouchPanel` → `WinRt.InjectPointer`; Back from `GamePad` |
| pure Silverlight | Avalonia `SilverlightRenderer` (`DrawingSurface` gets `BrandedSplashRenderer` or a placeholder) | Avalonia pointer events in `PhoneApplicationFrameView` |

Everything on the `TouchPanel` path already gets keyboard→touch bindings, wheel scrolling and the
driver picker. Pure Silverlight gets none of them and does not run on Android.

**No new abstraction is needed.** `GraphicsDevice` and `TouchPanel` are the layer. A neutral
`ITexture` / `IRenderTarget` vocabulary was rejected in CLAUDE.md ("Capabilities, not driver
names") and a touch seam was rejected in "Touchscreen and hardware buttons are NOT sensors"; both
reasons still hold.

### 2a. Decide `IBackgroundRenderer`: retire the D3D11 backend

`Plans/SILVERLIGHT-XNA-CONVERGENCE.md` §5 gates Stage C on this. Since that plan was written,
`Research/Wp8Native/XamlInterop.cs` implements `IDrawingSurfaceBackgroundContentProviderNative`
and replays native `DrawingSurfaceBackgroundGrid` content through the shared device. The D3D11
backend now draws only `D3D11ImageSplashRenderer` and `D3D11TestPatternRenderer`.

**Proposal:** retire `WPR.Backend.Direct3D11` and redefine `IBackgroundRenderer` to render into a
`Texture2D`, or drop it. Move the splash to `BrandedSplashRenderer` on the XNA device.
`BackendIsolationTests.AllowedReferrers` loses one entry.

**Done 2026-10-12** (branch `retire-d3d11-backend`): the project, the `ISurfaceRendererBackend`
seam, the `SilverlightBackend` registry and the never-called `RegisterRenderer` table are deleted.
`DrawingSurfaceBackgroundGrid.LookupRenderer` now tries `BrandedSplashRenderer` and otherwise
returns null, so `SilverlightRenderer` paints its status placeholder. The app's own
`SplashScreenImage*.jpg` is no longer drawn there (only the D3D11 path ever did). Nothing
references Vortice any more. `IBackgroundRenderer` stays, still Avalonia-shaped, because
`BrandedSplashRenderer` implements it; Stage C removes it with `SilverlightRenderer`.

### 2b. Run Stage C and Stage D of the convergence plan

As written there: route `ApplicationType.Silverlight` through `MixedModeGame` unconditionally,
delete `MixedModeDetection` and the `isMixedMode` branch in `ApplicationLaunch`, then delete
`SilverlightRenderer`, `PhoneApplicationFrameView`, `SilverlightLauncher` and the Avalonia
package reference (about 1,800 lines). Keep the Avalonia path behind a flag for one release, as
that plan says.

**Exit:** every in-process game draws through `GraphicsDevice` and reads touch through `TouchPanel`.

### 2c. WP8 native keyboard

`ICoreWindow` `add_KeyDown` / `add_KeyUp` are reached by Angry Birds Go! and unimplemented.
Raise them from `Keyboard.GetState()` edges in `Wp8NativeGame`, alongside the existing pointer
injection. Low priority: few WP8 titles used a keyboard. Easier after 1d names the slots.

**Done 2026-10-12** (`Research/Wp8Native/Keyboard.cs`): `add_`/`remove_` for `KeyDown`, `KeyUp`
and `CharacterReceived` keep every handler with a distinct token, AddRef on add and Release on
remove; events go out at the next `ProcessEvents` or XAML frame with a `KeyEventArgs`
(`VirtualKey`, `KeyStatus`, `Handled`). `GetKeyState`/`GetAsyncKeyState` answer real state.
`Wp8NativeGame.PumpKeyboard` injects `Keyboard.GetState()` edges (the Back-bound key excluded) and
`TextInputEXT` characters. Contract tests: `Suites/KeyboardSuite.cs`. Not modelled: `KeyDown`
auto-repeat.

### Not merged, on purpose

- `Research/Wp8Native/Direct3DRuntime.cs` translates guest D3D11 into XNA draws. It is a
  translator, not a second renderer.
- Unity ports and GameMaker run as separate processes.
- The launcher UI (Avalonia on Windows, native views on Android) is not game rendering.

## 4. Track 3: WP8 runtime duplicates

Each item is independent and small.

### 3a. Compressed textures: one decoder, not two

`FrameCapture.DecodeBlocks` expands BC1/BC2/BC3 to RGBA, and `Wp8GpuReplay` always creates
`SurfaceFormat.Color` textures. So WP8 titles decompress on the CPU even on the desktop, where
D3D11 supports BC natively. The XNA side already handles this: create the texture as
`Dxt1`/`Dxt3`/`Dxt5` and `TextureFormatShim` stores it compressed when the device can, and
decompresses through `DxtUtil` when it cannot.

Change the replay to create textures in their shipped format and pass the blocks through. Delete
`DecodeBlocks`. (It also recomputes the same palette three times in a `for (i < 3)` loop.)

**Check:** a 3D WP8 title's draw count and screen hash unchanged on desktop, and on an Adreno or
Mali device; `WPR_FORCE_NO_DXT=1` on desktop reproduces the phone path.

### 3b. Android audio through the registry

`AudioTrackOutput` exists because FAudio ran dry with small double-buffered chunks on phones. It
sits outside `AudioBackendRegistry`, so it never appears in the `[wpr-audio]` line, and XNA
titles that stream through `DynamicSoundEffectInstance` hit the same FAudio behaviour without the
fix.

Make it an `IAudioModule` in `Src/Modules/Audio/` that fills the dynamic-voice half of
`IAudioBackend` on Android and forwards the rest to the module below, the same shape
`AndroidMediaPlayerModule` uses for songs. Move `SincResampler` with it. `Wp8NativeAudio` then
goes through `DynamicSoundEffectInstance` on both heads.

**Check:** measure an XNA title that uses `DynamicSoundEffectInstance` on a phone before and after.
If it was not choppy before, the module only needs to cover WP8, and that is still worth doing for
the single registry.

### 3c. Lifecycle reaches the guest

`Wp8NativeGame.OnActivated` / `OnDeactivated` only pause audio. Nothing raises
`CoreApplication.Suspending`, `Resuming` or `CoreWindow.VisibilityChanged` to the guest, although
games subscribe to `VisibilityChanged` (slot 56, confirmed in `WinRtRuntime`). Raise them from the
same `Game.Activated` / `Deactivated` the WP7 path turns into `PhoneApplicationService` events.

**Done 2026-10-11** (`Research/Wp8Native/AppLifecycle.cs`): `OnDeactivated` raises
`CoreWindow.Activated(Deactivated)`, `VisibilityChanged(false)`, then `Suspending` with a working
deferral (held until `Complete()` or a 5 s deadline); `OnActivated` raises `Resuming`,
`VisibilityChanged(true)`, `Activated(CodeActivated)`. Delivered on the guest's thread at its next
`ProcessEvents` or XAML frame. Not done: a XAML shell's page-level `Application_Deactivated` steps.

### 3d. Vibration and launchers

**Done 2026-10-11** (`Wp8HostServices`): the WP7 link rule moved to `WPR.Engine.Launchers.WebLinkPolicy`
and both callers use it; vibration honours `VibrationBackend.IsEnabled`, and the motor is stopped on
deactivation and teardown.

`Windows.Phone.Devices.Notification.VibrationDevice` and `Windows.System.Launcher.LaunchUriAsync`
are not implemented. Wire them to `VibrationBackend.Device` and `LauncherBackend.Uri`, following
`Wp8NativeSensorHost` and `Wp8NativeXboxHost`. Apply the same http/https-only rule
`WebBrowserTask.Normalise` applies.

### 3e. Save folder path in one place

`ApplicationLaunch` builds `IsolatedStore/<ProductId>/AppFiles` inline for the WP8 sandbox. It
matches `PerGameIsolatedStorage.GameRoot` today, which is why Clear data and the uninstall prompt
already work for WP8 titles. Call `GameRoot` so the two cannot drift.

## 5. Track 4: spike — Unity and Spark2 on the ARM CPU

`Plans/Unity_WP8_Feasibility.md` closes in-process Unity on the grounds that ARM machine code
cannot execute on x64. With dynarmic in `WPR.Engine.Wp8Native`, that premise is out of date.
Spark2 (AC Pirates) was closed for the same reason.

A Unity WP8 title is a managed Silverlight shell, which WPR already hosts, plus a native ARM
`UnityPlayer.dll` that provides `DrawingSurfaceBackgroundGrid` content through
`UnityApp.GetBackgroundContentProvider()`. `XamlInterop` already implements the native provider
interface.

The missing piece is a bridge from managed shell calls into the guest: the shell activates
`UnityApp` and calls `SetBridge`, which today lands on a stub. The spike answers three questions,
in order, and stops at the first "no":

1. Can the loader map `UnityPlayer.dll` and run its DLL entry point to completion? (Track 1's
   traps will say what it imports.)
2. How large is the managed→native surface the shell actually uses? Count it with Cecil from
   Twins Minigame's `Twins.dll`; the feasibility doc names `WinRTBridge`, `UnityApp.SetBridge`
   and `GetBackgroundContentProvider`.
3. How large is the native→managed surface? `CppInvoke` calls from `UnityEngine.dll` back into
   the player cross the other way.

**Output:** a go / no-go section appended to `Unity_WP8_Feasibility.md`, with the counts. Do not
build the bridge as part of the spike.

## 6. Track 5: docs

- `Docs/ARCHITECTURE.md` lists ModernNative as "Not supported — out of scope" and describes the
  host as .NET 8. Both are wrong: WP8 native runs through `WPR.Engine.Wp8Native`, and the repo is
  on .NET 10.
- Add `WPR.Engine.Wp8Native`, `WPR.Engine.GameLoop`, `WPR.Engine.Input` and the Online modules
  to its project table.
- After track 2, `Plans/ARCHITECTURE-MIGRATION.md` §1.3 ("Two backends, not one") becomes one
  backend; update it in the same change that deletes the D3D11 project.
- After track 4, update `Unity_WP8_Feasibility.md` with the result either way.
- Add this file to `Plans/README.md`.

## 7. Risks

- ~~**1a regresses titles that run today.**~~ Resolved by making trapping opt-in
  (`WPR_WP8_TRAP_IMPORTS=1`); players keep the defaulting behaviour, now reported to the hub on
  failure. See the revision note under 1a. The remaining real risk is **delivered raises**: a
  `__abi_WinRTraise*` the old code swallowed now really throws, and twice so far the throw exposed
  a host bug that the swallow had hidden (ICoreApplication::add_Suspending answering E_NOTIMPL;
  vccorlib's heap not zeroing `ref new` storage). Watch `raised=[…]` in the summary line.
- **The SList hypothesis may be wrong.** 1c is worth doing regardless, since the functions are
  stubbed and every job system uses them. If Angry Birds Go! still faults after it, the trap from
  1a points at the next cause.
- **2a removes a capability someone may want.** The D3D11 backend's only remaining users are
  splash and test-pattern renderers; check no title uses `DrawingSurface` from managed code
  before deleting it.
- **3b on its own can change audio on every Android XNA title** that uses dynamic instances.
  Measure first, as noted.
- **Track 4 is open-ended.** Time-box the spike.

## 8. Order

1. 1f, 1e "now", 3e — trivial, ship anytime.
2. 1a + 1b together, then 1c.
3. 3a, 3c, 3d — independent.
4. 1d, then 2c.
5. 2a decision, then 2b.
6. 3b after measuring.
7. Track 4 spike once track 1 is in.
8. Track 5 alongside whichever change makes each doc wrong.
