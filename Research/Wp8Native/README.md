# WP8 native probe

Exploratory. **Not part of `WPR.sln`** — build it directly, it has no project references
and nothing references it.

WP8 "Modern Native" XAPs (`RuntimeType="Modern Native"`) contain no managed code, so
`ApplicationPatcher` has nothing to rewrite and `ApplicationInstaller` rejects them with
`ModernNativeUnsupported`. The only way such a title could ever run under WPR is to
emulate the ARM CPU and implement the boundary it calls across. This probe establishes
whether that boundary is a reasonable size.

It loads an ARMv7 Thumb-2 PE, maps it, redirects every IAT slot into a trap page, runs
the real entry point on an emulated CPU, and reports every call that leaves the binary.

## Running it

From Windows, one command:

```powershell
.\run.ps1 -Game C:\wp8\abrio\AngryBirdsRio.exe
```

`run.ps1` builds, runs, and puts the report on your console. It runs the probe **natively**
when a `unicorn.dll` is sitting next to it, and otherwise builds for linux-x64 and runs it
under WSL - which is transparent apart from a line saying which it picked.

Useful switches, all of which map to the environment variables the probe reads:

| switch | what it does |
| --- | --- |
| `-Budget 3000000000` | instruction budget; this one gets Angry Birds Rio a few hundred frames in |
| `-Screenshot .\frame.png -Frame 200` | rasterise one presented frame to a PNG, copied back to Windows |
| `-Trace 0x00534E3A,0x00462494` | log the register file every time those instructions run |
| `-Watch 0x60003A0C:16` | log every write into that address range, host-side writes included |
| `-Files 400` | show more of the file-access log than the default twenty lines |

Point `-Game` at the executable inside an unpacked XAP - a XAP is a plain zip - or set
`WPR_GAME` and leave the switch off.

Without the script it is just a console app:

```
dotnet run -c Release -- <path-to-arm-exe> [instruction-budget]
```

### The native library problem

`UnicornEngine.Unicorn` 2.1.3 ships native runtimes for **linux-x64, linux-arm64,
linux-ppc64le and osx-x64 only**. There is no `win-x64` `unicorn.dll` in the package, so
without one the static analysis runs and then the CPU fails to start with
`DllNotFoundException`, which `Program` catches and explains rather than letting it look like
a crash.

Nothing else about the probe is platform-specific. The binding is the only P/Invoke and it
imports the name `unicorn`, which .NET resolves to `libunicorn.so` on Linux and `unicorn.dll`
on Windows. **That one file is the entire difference between WSL-only and native.**

Drop a `unicorn.dll` into this directory and it is picked up without further ceremony: the
csproj copies it to the output and `run.ps1` switches modes. Nothing in the C# changes.

#### Which Windows build

Not the obvious one. The Unicorn 2.1.3 release page offers four Windows assets, and the two
worth knowing about behave very differently:

| asset | verdict |
| --- | --- |
| `windows-msvc64-shared.7z` (41 MB) | **Unusable.** It is a *debug* build - the DLL imports `VCRUNTIME140D.dll` and `ucrtbased.dll`, which are not redistributable and ship only with Visual Studio. Both copies in the archive (`bin/` and `Debug/`) are the same debug binary; there is no release build in it. |
| `windows-mingw64-shared.7z` (17 MB) | **This one.** `bin/libunicorn.dll` imports only `KERNEL32`, `kernelbase` and `msvcrt` - all present on any Windows install. Rename it to `unicorn.dll`, because .NET's `lib` prefix probing is Unix-only. |

#### One Unicorn bug to know about

`uc_emu_start` with a real `until` address **crashes the process** on that MinGW build - no
managed exception, no stderr, the process simply dies partway through printing its report.
The same call is fine on the linux-x64 build from the NuGet package, which is why it went
unnoticed until the probe first ran natively.

`ArmEmulator.Run` therefore does not pass `until` to Unicorn at all. It installs a code hook
at the stop address which calls `EmuStop`, which does the same job, costs nothing until the
address is reached, and behaves identically on every build. The API keeps the `until` shape,
so no caller has to know.

#### Is it the same?

Yes, and checked rather than assumed. Native and WSL runs of Angry Birds Rio produce the same
report - 240 dispatcher turns, 238 frames, the same stopping point at `0x0044F0EF` - and the
rasterised frame is **byte-for-byte identical**, same MD5 from both. Native takes about
thirteen seconds for three billion instructions, which is roughly what WSL takes.

#### Why bother, when WSL works

A debugger. Under WSL the probe is a separate Linux process and Rider cannot put a breakpoint
in `HostStubs` or step into a trap handler. Natively it is an ordinary .NET console app, and
every stub becomes something to break on, inspect and edit. For work that is mostly "what did
the image actually pass here", that is the whole difference.


## What it currently does

Against `AngryBirdsRio.exe` (Angry Birds Rio v2.2.0.0), the run reaches the game's first
WinRT activation request in 124 translated blocks:

```
 1. GetSystemTimeAsFileTime      \
 2. GetCurrentThreadId            |  __security_init_cookie
 3. GetTickCount64                |
 4. QueryPerformanceCounter      /
 5. __crtGetShowWindowMode       \
 6. _initterm_e                   |  CRT initialisation
 7. _initterm                    /
 8. Platform::GetCmdArguments    \
 9-12. Heap::Allocate, Object::Object
13. CoCreateFreeThreadedMarshaler |  C++/CX bootstrap
14-19. GetIBoxArrayVtable, ...   /
20. GetActivationFactoryByPCWSTR  <-- "give me a WinRT class"
```

That request is now answered — see the vtable bridge below.

## The vtable bridge

The import census undercounts the real surface, in a way that turns out to be good news:
the image imports `d3d11.dll` exactly once, but will make hundreds of D3D calls. Almost
everything interesting arrives through **COM vtables**, not the import table.

A COM object is a pointer to a vtable, and a vtable is an array of function pointers.
Neither has to be real code, so `WinRtRuntime` builds them in emulated memory:

```
object  ->  [ vtable pointer ][ refcount ]
vtable  ->  [ &trap0 ][ &trap1 ][ &trap2 ] ...
```

Each slot points at a trap, so when emulated code does the usual
`ldr r2,[r0] ; ldr r3,[r2,#n] ; blx r3`, the host runs the method and `bx lr` returns.
Every WinRT interface derives from IInspectable, so slots 0-5 are always QueryInterface,
AddRef, Release, GetIids, GetRuntimeClassName, GetTrustLevel, and an interface's own
methods start at slot 6.

`Windows.Phone.System.Memory.MemoryManager` is implemented as the worked example: two
read-only properties on top of IInspectable, small enough to be unambiguous. `VtableProof`
assembles the exact instruction sequence MSVC emits for a WinRT property getter, runs it
on the emulated CPU, and checks the result:

```
vtable slot 6  get_ProcessCommittedBytes
    HRESULT          0x00000000 (S_OK)
    value returned   50,331,648 bytes (48 MB)
    -> PASS
```

A failure there fails the whole run (exit code 3).

### The game drives the bridge too

More convincing than the harness: `CoreApplication` is registered as a shaped-but-empty
object, so the game's own code now gets a factory back, and calls straight through the
synthesised vtable:

```
ICoreApplication::slot13  this=0x60001030 r1=0x60001114 r2=0x502FFF40
ICoreApplication::Release
```

`this` is our factory. `r1` points into the emulated heap — an object the game built
moments earlier through the `Platform::Heap::Allocate` stub. `r2` is stack noise, so the
method takes one argument. One argument, an object pointer, at vtable slot 13: by the
documented ICoreApplication member order that is **`Run(IFrameworkViewSource*)`**, the
entry point of every C++/CX WinRT app.

The bridge is not a simulation of a call; it is the call.

## The callback bridge

`CoreApplication::Run` needs the opposite direction — the host has to call
`CreateView` on an IFrameworkViewSource made of the image's own ARM code. That cannot be
done by re-entering the CPU, because the host is *already* inside a hook when it needs to
make the call, and nesting `emu_start` is not safe.

The way round it is the instruction sitting in every trap slot. It is `bx r12`, not
`bx lr`, and `OnTrapEntered` presets r12 to lr — so by default a trap returns to its
caller, but a handler that overwrites r12 with a target and lr with a fresh return trap
turns that same instruction into a **tail call into emulated code**. Control comes back
when the emulated function returns. Nothing re-enters, so it works from inside a hook.

`ArmEmulator.CallEmulated` packages that up. The one contract: the `onReturn` callback
must set r12 (usually via `ContinueAt`), because lr still points at the return trap.

It works with the image's real code:

```
ICoreApplication::Run -> calling IFrameworkViewSource::CreateView at 0x00446A21
...the image's own CreateView runs, ~9,000 blocks of it...
IFrameworkViewSource::CreateView returned HRESULT=0x00000000 view=0x60001244
```

`0x00446A21` is inside `.text`; `0x60001244` is an IFrameworkView the image built and
handed back. `VtableProof.RunCallbackProof` also covers the same path deterministically
against a synthesised view source, so a regression fails the run rather than hiding.

## The startup chain, as discovered

Everything below was found by running the image, not by reading documentation. Classes
registered through `RegisterDiscoveryClass` log their arguments and report success, so
execution keeps going and reveals what comes next:

| what the image did | how it was identified |
| --- | --- |
| `CoreApplication::Run(viewSource)` | vtable slot 13, one object-pointer argument |
| `DisplayProperties` slot 9, arg `5` | a *value*, not a pointer — `put_AutoRotationPreferences` with Landscape\|LandscapeFlipped, which is what a landscape-only game sets |
| `ApplicationData` slot 6 → an object | `get_Current`, then a further property off the result |
| `AddRef` / `Release` / `QueryInterface` | C++/CX refcounting the objects correctly throughout |
| `IFrameworkView::Initialize` runs | subscribes to OrientationChanged, Activated, Suspending, Resuming |
| `HardwareButtons` slot 6, `(handler, token*)` | `add_BackPressed` — the WP bezel Back button |
| `ThreadPool::RunAsync(handler, out)` | queues background work — see "Threads, without threads" |
| ConcRT `critical_section`, `event`, `_ScheduleTask` | the image uses PPL tasks as well as the WinRT pool |

Placeholder objects matter more than they look: an unimplemented method that hands back
**zero** sends the image through a null vtable and execution stops at address 0. Handing
back a shaped object instead took the run from 195 blocks to **9,089 blocks / 85,608 bytes
of executed code**, and is what let CreateView complete at all.

Throughput on the synthetic Thumb-2 loop is around **1,000 MIPS**, which is the same
order as the 1.0–1.5 GHz hardware these titles shipped on — and rendering would not be
emulated at all, since D3D11 calls would forward to the host GPU.

## The view lifecycle

With the callback bridge working, `CoreApplication::Run` can do its real job: walk the
image's IFrameworkView through `Initialize` → `SetWindow` → `Load` → `Run`, passing it an
ICoreApplicationView and an ICoreWindow.

Each step has to wait for the previous one to finish, and "finish" here is an
asynchronous event — control only comes back when the emulated function returns. So the
sequence is a **continuation chain**, not a loop: each step's completion handler starts
the next, and the last one hands control back to whoever called `Run`. `Uninitialize` is
deliberately not in the chain, since `Run` is the game's main loop and is not expected to
return.

`VtableProof` covers the whole sequence against an IFrameworkView built out of real
Thumb-2, where each of the four methods stamps its own number into a buffer — so the
emulated side leaves evidence, rather than the host merely logging its own intentions:

```
ICoreApplication::Run
    view received    0x6019F630     resumed caller  yes
    lifecycle driven on the emulated side:
        IFrameworkView::Initialize  ran
        IFrameworkView::SetWindow   ran
        IFrameworkView::Load        ran
        IFrameworkView::Run         ran
    -> PASS
```

`ICoreWindow` is registered wide (48 slots) and left to discovery; `ICoreApplicationView`
has slot 6 wired to `get_CoreWindow` up front, since handing back a placeholder there
would split the app across two different windows.

## The CRT has to be real

Stubs that return zero are survivable for a handle nobody checks. They are not survivable
for `memcpy`, `strlen` or `WindowsCreateString`: those do not fail loudly, they quietly
corrupt whatever the image was assembling, and the damage surfaces much later as a jump
to a garbage address. `HostStubs` now implements `memcpy`, `memmove`, `memset`, `memcmp`,
`strlen`, `wcslen` and the full HSTRING family for real.

An HSTRING is opaque, so its representation is ours: a pointer to
`[length:UINT32][UTF-16 chars, null terminated]`, which lets `GetStringRawBuffer` hand back
an interior pointer without copying. Every handle issued is tracked, and **anything not
issued by us is treated as the empty string** — because the image will cheerfully pass a
discovery object back as though it were a string, and reading a length out of one yields
its vtable pointer, a nonsense length that turns the next copy loop into a hang.

### The parse-and-throw loop, and what actually caused it

For a while the image span inside `CreateView` until the instruction budget ran out:

```
most called:                          after implementing CrtLibrary:
    23,279  operator new                  304  memcpy
    19,958  memcpy                         78  operator new
     3,326  std::exception::exception      18  isdigit
     3,325  isdigit                         -  std::exception::exception
```

The obvious suspect was the placeholder `ApplicationData` feeding it a garbage path. It
was not. The cause was the C runtime: `isdigit` returned 0, so nothing was ever a digit,
and `strcmp` returned 0, so every pair of strings compared **equal**. Parsers rejected
valid input, threw, and retried — 3,326 exceptions in one run.

`CrtLibrary` implements the character, string and number functions properly, in the C
locale. That removed the loop outright: 59,908 import calls became 617, and `CreateView`
went from spinning forever to returning normally.

The general lesson is the same one as `memcpy`, only sharper. A stub that returns zero is
not neutral. For a predicate it is a confident *no*, and for a comparison it is a
confident *equal*, and both are answers the image believes.

## Real storage

`Windows.Storage.ApplicationData` is implemented rather than shaped, because the image
reads a path out of it during `CreateView` and then parses it.

The slot numbers were read off the trace rather than guessed. The image called `get_Current`
at slot 6, then slot 12 on the result, then a `QueryInterface` and slot 12 again — which is
`IApplicationData::get_LocalFolder` followed by `IStorageItem::get_Path`, and the documented
member order for both agrees. The trace now names them:

```
IApplicationDataStatics::get_Current
IApplicationData::get_LocalFolder
IStorageItem<Local>::QueryInterface
IStorageItem<Local>::get_Path      -> C:\Data\Users\DefApps\AppData\{...}\Local
```

The folder objects are laid out as `IStorageItem`, because that is the interface the image
queries for. A real StorageFolder implements several interfaces with different vtable
layouts and this cannot: `QueryInterface` returns the same object whatever IID it is
asked for, so only one layout can be right at a time. Per-IID vtables are the honest fix
when a second interface is needed.

## Threads, without threads

A Unicorn context is one CPU with one set of registers and one stack, so there is no
honest way to run anything *concurrently*. Both `Windows.System.Threading.ThreadPool` and
ConcRT's `_ScheduleTask` hand over work expecting it to run elsewhere while the caller
carries on, and the answer to both is the same: **queue it, and run it after the caller
has finished** rather than during.

Running work inline was the obvious first attempt and it is measurably worse than not
running it at all. The image queues background work part-way through building something,
so a handler invoked before `RunAsync` returns sees half-initialised state and dies
immediately — having made no calls at all, which leaves nothing in the trace to explain
it. Deferring is both more faithful to the contract and what actually gets past it.

The queue lives on `ArmEmulator` (`QueueDeferredCall` / `DrainDeferredCalls`) since two
unrelated subsystems feed it, and it drains **between view lifecycle steps** — the one
moment the image is known to have finished a phase.

What this cannot survive is a work item that expects to run *while* the caller continues:
a producer/consumer handshake becomes a deadlock rather than a wait. Real scheduling would
need saved CPU contexts, which the .NET binding does not expose.

### Locks are the easy part

With one thread of execution nothing is ever contended, so `critical_section::lock`,
`unlock` and `scoped_lock` are no-ops — not an approximation, the correct answer.
`event::wait` is the awkward one: returning "signalled" immediately is a lie if something
else was supposed to signal it, and there is no something else. It reports signalled
because the alternative is to block forever.

### A constructor returns `this`

The bug that had `Initialize` dying for three iterations, and the sharpest instance yet of
the zero-stub problem.

An MSVC C++ constructor returns `this` in r0, and the caller uses **that** value, not the
`operator new` result. So an unimplemented constructor returning 0 hands back a null
object, and the caller dereferences it a few instructions later with nothing in the trace
to explain why. `Dispatch` now preserves r0 for any name beginning `??0`, which makes an
unimplemented constructor merely uninitialised rather than fatal.

With that and the ConcRT primitives in place, `IFrameworkView::Initialize` runs to
completion and returns S_OK:

```
view lifecycle:
    -> IFrameworkView::Initialize at 0x00451A65
       IFrameworkView::Initialize returned 0x00000000

deferred work (threads stand in as queued callbacks):
    queued IWorkItemHandler::Invoke at 0x004A4B2D (1 pending)
    -> IWorkItemHandler::Invoke at 0x004A4B2D
```

The background work item is then dispatched, and does not return — which is where the
next stretch of work starts.

## Following the work item

The background work item dispatched by `RunAsync` did not return. Chasing it took three
tools that did not exist yet, and turned up two bugs in this probe and one in its
assumptions.

**A ring of recent basic blocks.** A final PC says a null pointer was called; the trail
leading to it names the code that did. It showed the work item at `0x004A4B2C` calling
into `0x004A47AC` and dying there.

**The stop address was mine, not the image's.** `EmuStart(start, until, ...)` takes the
stop address literally, and this passed **0**. So every run had been halting the moment
the CPU reached address 0 — a null call — while reporting an exhausted budget. Two
symptoms for the price of one bug: the null call was invisible, and the budget looked
spent when barely any of it was.

**No static initialiser had ever run.** `_initterm` and `_initterm_e` were stubbed to
return 0, which claims every initialiser succeeded without running any. Every global C++
object in the image was therefore unconstructed, and the work item was calling a method on
one of them. They can be run properly now that a host trap can call back into emulated
code — each initialiser is a separate call that must finish before the next starts, so it
is another continuation chain.

Running them exposed two more zero-stubs, both fatal in the same quiet way:

| function | what zero means | what it should be |
| --- | --- | --- |
| `EncodePointer` | the CRT stores a **null** function pointer, decodes null, calls it | identity — the encoding only obfuscates against overwrite attacks |
| `_calloc_crt` | allocation failed | allocate |

With those, **195 static initialisers run** where 2 did before.

### A catch must restore the frame's registers

The next null call after the data-import fix was a virtual call on an object that looked
constructed but was not:

```
0x00444212  ldr r3, [r4]      ; r3 = object[+4]   -> 0
0x00444216  ldr r3, [r3, #4]  ; r3 = 0[+4]        -> 0
0x00444218  blx r3            ; null
```

The object at `0x600023A0` had a real vtable at +0 (`0x0059B594`, inside the image) but a
zero where a second interface pointer belonged — the signature of a half-initialised
object rather than a missing implementation.

The cause was in the transfer. Real exception handling restores the **establisher frame's
callee-saved registers** before entering a catch, because the code after the catch belongs
to that frame and expects its own r4-r11. This restored only the stack pointer, so the
continuation ran with whatever the last of sixteen cleanup funclets had left behind — and
an object built out of those registers came out wrong.

The values were already to hand: unwinding recovers each frame's registers on the way
past, which is what `UnwindState` exists for. `UnwoundFrame` now carries that snapshot and
the transfer puts r4-r11 back before calling the funclet.

The result, against the run before it:

| | before | after |
| --- | --- | --- |
| blocks executed | 14,783 | **15,959** |
| import calls | 661 | **739** |
| distinct imports | 50 | **62** |

Newly reached: `__getmainargs`, `IsProcessorFeaturePresent`,
`__crtSetUnhandledExceptionFilter`, and the data imports now doing their job. More to the
point, execution reaches `0x004A4B2C` — **the deferred work item**, the background task
queued during `IFrameworkView::Initialize`, finally running.

## PPL task collections

`Concurrency::task` sits on a small set of exported primitives, and the work item uses
them: `_AsyncTaskCollection::_NewCollection`, `_TaskCollection::_Schedule` and
`_RunAndWait`, `_Cancel`, and the cancellation-token pair. All are implemented — a
collection is a stand-in object, `_RunAndWait` reports `_Completed`, and a cancellation
registration is an object rather than a null.

Three things came out of writing it, generalised so they apply beyond PPL:

- **`CreateShapedObject` / `CreateShapedVtable`.** The answer to "this returns an object
  and does not" is not zero — the caller dereferences that immediately. A stand-in with a
  real vtable of traps keeps the image running and names whatever it calls.
- **An unimplemented constructor now leaves a usable object.** Returning `this` is
  necessary but not sufficient: the object is still exactly as the allocator handed it
  over, all zeros, so the first virtual call goes through a null vtable a long way away.
  A shaped vtable is written into any object whose first word is still zero.
- **A stand-in object is 256 bytes, not 8.** The image believes it holds a real class and
  reads and writes members at that class's offsets — `[this + 0x18]` and beyond. A small
  stand-in sends those reads into the *next* allocation, which returns a neighbour's data
  where the image expects an unset zero, and sends those writes into corrupting it.

### It did not fix the null call

All three are real improvements and none of them moved the failure. The block count is
identical to the digit across all three attempts — 15,959 — which is itself the finding:
none of this is on the failing path.

## Tracing an allocation

Every block in the emulated heap comes through one bump allocator, so recording the
requester makes "who allocated this address" a lookup rather than an inference.
`DescribeAllocation` reports the containing block, its size, the stub it was requested
through, and the emulated return address that asked:

```
0x60002ED0 +0xC, 80 bytes, from MSVCR110.dll!??2@YAPAXI@Z, requested by code at 0x004A35DA
block starts 00 47 63 00  00 00 00 00  00 00 00 00  00 00 00 00 ...
```

That is worth more than the address itself. The `+0xC` says the dead pointer is not a
member that was left null — it is an **interior pointer** to a subobject twelve bytes into
someone else's allocation. Disassembling the requester shows a factory:

```
0x004A35D2  movs r0, #0x48        ; 72 bytes
0x004A35D6  bl   #0x57fe28        ; operator new
0x004A35DC  cbz  r0, #0x4a35e4    ; skip construction if it failed
0x004A35DE  bl   #0x4a38a8        ; the constructor
0x004A35E6  adds r3, r0, #0xc     ; interior pointer to the subobject
0x004A35EA  str  r3, [r4]         ; returned as the result
```

And the constructor at `0x4A38A8` sets two fields to 1 and installs vtables — including
the subobject's at +0xC.

### What the block says

```
+0x00  0x00634700   a vtable, but not the 0x00641630 that constructor writes
+0x04  0            the constructor sets this to 1
+0x08  0            the constructor sets this to 1
+0x0C  0            the subobject vtable the destructor calls through
```

The refcounts are zero and the vtable has been swapped for a different one. That is the
signature of an object which has already been torn down, being torn down again — the
destructor finds a subobject whose vtable was cleared by an earlier pass.

**The obvious suspect was wrong.** If the unwind ran a cleanup funclet the image also runs,
that would double-destroy exactly like this. Skipping the catching frame's cleanups
entirely changes 16 funclets to 15, moves the block count by four, and leaves the null call
in the same place. Not it.

So the allocation is identified and the shape of the corruption is clear, but the
responsible code is not. The tools to continue are in place: allocation provenance, the
disassembler, and the write watch.

## The work item dies — and no longer takes the run with it

Inside the deferred work item, at `0x004A47F1`, in what the disassembly shows to be a
destructor:

```
0x004A47DE  str r3, [r4]        ; set the object's vtable
0x004A47E0  ldr r3, [r4, #0x18] ; a member pointer
0x004A47E4  beq / cbz           ; skip if it is 0 or 1
0x004A47E8  mov r0, r3
0x004A47EA  ldr r3, [r3]        ; its vtable -> 0
0x004A47EC  ldr r3, [r3, #8]    ; slot 2      -> 0
0x004A47EE  blx r3              ; null
```

The member at `[r4 + 0x18]` points at `0x60002E1C`, a heap block that is **all zeros** —
allocated and never initialised. The destructor sees a non-null pointer, takes neither
skip, and calls through a vtable that was never written.

What is known: the object being destroyed is the image's own (the vtable it installs is in
`.data`), the zeroed block was allocated before any stand-in object, and it is not the
product of an imported constructor — giving those shaped vtables changed nothing. What is
not known is which code owns that member and should have filled it in.

The measurement that says so: three separate fixes this round — task collections, shaped
constructors, larger stand-in objects — and the block count stayed at 15,959 to the digit.
None of them is on the failing path.

The RTTI settles what the objects are, which the earlier guesswork did not. The block at
`0x60002ED0` is a `std::_Ref_count_obj<_Task_completion_event_impl<bool>>` — a shared_ptr
control block. Its constructor writes vptr `0x00641630` and sets `_Uses` and `_Weaks` to 1;
what is there at the crash is vptr `0x00634700`, which is `std::_Ref_count_base`, with both
counts at zero. That is precisely the state MSVC leaves behind after `~_Ref_count_obj` has
run: the derived destructor rewrites the vptr to its base before chaining. The object was
not "never initialised", as the paragraph above assumed — **it was destroyed, and the PPL
continuation handle at `0x60002F60` still holds a pointer into it.** A use-after-free, and
the over-release that caused it is still unattributed.

### Fault domains: a dead callback is a dead thread, not a dead process

The work item is a background thread. On a device it would die on its own and the app would
carry on; here it stopped everything, and the foreground — where the lifecycle lives — never
ran at all.

`ArmEmulator` now wraps every deferred callback in a **fault domain**: the core register
file is snapshotted before the callback is entered, and a null call inside one abandons the
callback rather than the run. The CPU is put back exactly as it was, `r0` is set to
`E_UNEXPECTED`, and execution resumes at the callback's return trap — so the continuation
chain behind it proceeds as if the callback had returned a failure.

Only deferred callbacks get one. The view lifecycle runs on what a device would call the UI
thread, and a null call there is fatal to the app, exactly as it would be on hardware. That
distinction is the whole point: containing everything would just be ignoring faults.

The cap is eight. "Resume and carry on" is only useful while the deaths are independent; a
callback that dies, is abandoned, and immediately queues another copy of itself would
otherwise spin forever.

## The view lifecycle runs to completion

With the work item contained, `SetWindow` was reached for the first time — and died
immediately, executing heap data at `0x60001490`. The cause was three instructions:

```
0x0044AB26  ldr   r3, [r5]           ; the window's vtable
0x0044AB2E  ldr.w r3, [r3, #0xe0]    ; slot 56
0x0044AB34  blx   r3
```

`ICoreWindow` was registered as a 48-slot discovery object. Slot 56 is past the end of that
vtable, so the read ran off into the next object on the heap and the CPU jumped to whatever
it found. Widening the object to 64 slots fixed it, and the run went straight through
`SetWindow`, `Load` and into `Run`:

```
-> IFrameworkView::Initialize at 0x00451A65
   IFrameworkView::Initialize returned 0x00000000
-> IFrameworkView::SetWindow  at 0x00451B79
   IFrameworkView::SetWindow  returned 0x00000000
-> IFrameworkView::Load       at 0x0045195D
   IFrameworkView::Load       returned 0x00000000
-> IFrameworkView::Run        at 0x00451AD1
```

**This is the lesson a shape-only object is supposed to teach, and it only teaches it if the
shape is big enough.** A vtable that is too short does not fail as "unimplemented method" —
it fails as arbitrary code execution several thousand instructions later, in a completely
different part of the image.

### The slot numbering is now confirmed, not assumed

The convention throughout is that `IInspectable` occupies slots 0-5 and the interface's own
members follow in IDL order. That was an assumption. It is now confirmed independently on
five interfaces, because in every case the slots the image actually calls are exactly the
ones a game would want and nothing else:

| interface | slots called | what the member order says they are |
| --- | --- | --- |
| `ICoreApplication` | 7, 9, 13 | `add_Suspending`, `add_Resuming`, `Run` |
| `ICoreApplicationView` | 7 | `add_Activated` |
| `IHardwareButtonsStatics` | 6 | `add_BackPressed` |
| `IDisplayPropertiesStatics` | 9, 10, 12 | `put_AutoRotationPreferences`, `add_OrientationChanged`, `get_ResolutionScale` |
| `ICoreWindow` | 7, 30, 44, 46, 48, 56 | `get_Bounds`, `add_Closed`, `add_PointerMoved`, `add_PointerPressed`, `add_PointerReleased`, `add_VisibilityChanged` |

Five `add_` slots on `ICoreWindow`, all even, all landing on events a game subscribes to —
and `put_AutoRotationPreferences` called with `5`, an orientation bitmask, rather than a
pointer. Coincidence would have to work quite hard.

Those members are now implemented rather than shaped: `get_Bounds` returns 480x800,
`get_Dispatcher` hands back a real `ICoreDispatcher`, `get_Visible` and `get_IsInputEnabled`
return true, `GetKeyState` returns `CoreVirtualKeyStates::None`, and `get_ResolutionScale`
returns `Scale100Percent`. That last one matters more than it looks: the discovery default
answers a stack out-parameter with a placeholder *object pointer*, which as a scale factor
is about 1.6 billion percent.

### CoreWindow is asked for by name as well as handed over

`SetWindow` receives the window, and then the image calls `CoreWindow::GetForCurrentThread()`
— which activates `Windows.UI.Core.CoreWindow` and calls slot 6 on `ICoreWindowStatic`. That
class was not registered, vccorlib raised `ClassNotRegistered`, and the game died inside its
own main loop. Both paths now hand back the **same** window object; a second one would split
the game across two windows, subscribing to input on one and asking the other for its bounds.

### A raise that returns is worse than no raise at all

`__abi_WinRTraiseClassNotRegisteredException` was falling through to the default stub, which
returns zero. A function whose entire job is to throw does not return, so the caller carried
on with a null factory it had been promised it would never see, execution left the image, and
**22 MB of the "code executed" figure was a Thumb no-op slide through zeroed heap** — which
also filled the block ring with garbage and hid the real last call.

Making every `raise` stub fatal turned out to be too blunt: `__abi_WinRTraiseNotImplementedException`
is called twice on a path the image recovers from, and stopping there cost three lifecycle
steps. They are now counted and reported instead — the run says how many throws it was asked
for and did not deliver, which is the honest half of what can be done without wiring vccorlib
into the C++ exception machinery.

## Direct3D and DXGI

`Direct3DRuntime.cs`. The image now gets a device, a swap chain on its CoreWindow, and a
render target view of the back buffer:

```
D3D11CreateDevice(driverType=1, flags=0x0) -> device, context, feature level 9_3
Device::QueryInterface(IDXGIDevice1)
GetParent({2411e7e1-...}) -> adapter
GetParent({50c83a1c-...}) -> factory
CreateSwapChainForCoreWindow(window=...) -> swap chain
IDXGISwapChain::GetBuffer(0, {6f15aaf2-...}) -> back buffer
ID3D11Device1::CreateRenderTargetView
ID3D11DeviceContext1::RSSetViewports -> 480x800
```

Two things make this layer easier than the WinRT side rather than harder. These are plain
COM, so `IUnknown` takes three slots where an `IInspectable` takes six. And **most of the
surface returns `void`** - a WinRT stub that does nothing is a lie the caller eventually
notices, but `IASetVertexBuffers` that does nothing is exactly what a renderer with no output
device does, and the caller has no way to tell. What that turns the layer into is a recorder:
draw calls, clear colours, the viewport, and which resources were made.

`Map` is the one context method that cannot do nothing, because the image writes vertices
through the pointer it hands back. One 4 MB scratch buffer serves every map; nothing reads
any of it.

### QueryInterface has to be real here

The WinRT side of this probe answers QueryInterface with the same pointer for any IID, which
holds only because those statics implement one interface each. It does not hold for a moment
in D3D: the image queries the device for `IDXGIDevice`, the swap chain buffer for
`ID3D11Texture2D`, and answering either with the original vtable sends the next call to an
unrelated method. So objects here have an identity and a set of interface pointers, IIDs are
matched against a table, and **an unrecognised IID is refused rather than guessed** - a caller
that asked for something specific and got a yes will call a method that is not there, and a
wrong vtable is far harder to diagnose than a refusal it has to handle.

Derived interfaces are the same pointer: `ID3D11DeviceContext1` *is* `ID3D11DeviceContext`
with more slots on the end. So the widest member of each family is built once and aliased.
WP8 is a Direct3D 11.1 platform and this image asks only for the 11.1 IIDs.

### Short vtables, three times, and the fix

`ICoreWindow` at slot 56, `ID3D11DeviceContext1` at slot 116, `ID3D11RenderTargetView` at
slot 8: three times a vtable was one or more slots shorter than the interface, and every time
the failure looked like something else entirely. The read runs off the end into whatever is
next on the heap and the CPU jumps to it, thousands of instructions from anything related.

Two changes make the whole class of mistake cheap to survive:

- **Vtables are shared per interface, not per object.** Every slot costs a trap slot and the
  trap page is bounded; a game creating a thousand textures would exhaust it several times
  over with a private copy of the same eleven entries each. Sharing is possible because the
  handlers resolve their object from the `this` pointer in r0 rather than a captured
  reference, which is also what a real COM implementation does.
- **Every vtable carries eight slots of padding** past its last known method, and a call
  landing in the padding logs *"the slot map is wrong, not just unimplemented"* instead of
  jumping into the next object.

## The loader thread was running the wrong function

The largest find of this round, and it had two layers of symptom on top of it.

`IWorkItemHandler::Invoke` was being read from **slot 6** of the handler's vtable, on the
same reasoning that works everywhere else in WinRT: `IInspectable` occupies 0-5 and the
interface's own members follow. That reasoning does not apply to delegates.

**WinRT delegates are the one part of WinRT that is not `IInspectable`-based.**
`IWorkItemHandler`, `TypedEventHandler`, `AsyncActionCompletedHandler`, every `add_X` handler
- all derive from plain `IUnknown`, so the vtable is QueryInterface, AddRef, Release, Invoke,
and **Invoke is slot 3**.

Slot 6 of a four-slot vtable is not a missing method. In this image the delegate's vtable is
followed immediately in `.rdata` by the vtable of `_ContinuationTaskHandle`, so slot 6
resolved to that class's scalar deleting destructor - `void* dtor(unsigned flags)`, frees if
`flags & 1`, returns `this`. Every "work item" this probe ran was in fact the image tearing
the work item down. It then died on the way out, which is the null call at `0x004A47F1` that
two earlier rounds spent trying to attribute to a use-after-free.

And that was only the first layer. With the loader dead, the game's main loop spin-waited on
a byte the loader was supposed to set, calling `Concurrency::wait` round and round: **2.8
million import calls in one 8-million-instruction budget**, and a report that said only that
the budget had run out.

The lesson worth keeping: the slot map is the single most load-bearing assumption in this
whole probe, and it is the one that fails silently. Every wrong slot in this round -
`ICoreWindow` 56, the delegate 3, `HandlerType`'s stride - presented as a crash somewhere
else, and none of them presented as "unimplemented".

## A stub with an out-parameter must write it

All ten COM imports (`CoGetObjectContext`, `CoCreateFreeThreadedMarshaler`, `CoCreateGuid`,
`CoTaskMemAlloc` and friends) were falling through to the default stub, which returns S_OK
and writes nothing. That is the worst possible answer: the caller is told it succeeded and
reads back whatever was already in its own memory.

PPL captures the COM apartment for a continuation with `_ContextCallback::_Capture`, which is
`CoGetObjectContext` straight into a member, and sets the member to null only if the call
*fails*. Succeeding without writing left it holding a stale heap address; the matching
`_Reset` then saw a pointer that was neither null nor the deferred-capture sentinel of 1 and
called `Release` on it.

The rule now encoded in `HostStubs`: **a stub for a function with an out-parameter either
writes the out-parameter or returns a failure the caller must handle. It may never do
neither.**

## Yield points, or: deferred callbacks that can actually hand off

Draining queued work between view lifecycle steps works right up until the image enters its
main loop and never leaves it. From there the game polls a flag that background work is
supposed to set, and with no yield point the queue behind that flag never runs.

`Concurrency::wait` - the image's only sleep, and the point where it says it is willing to be
interrupted - now runs one queued callback and returns. That is cooperative scheduling: work
runs only where the image invited it, which on a single CPU is the only honest option.

`_TaskCollection::_Schedule` feeds that queue. A ConcRT chore is **not** dispatched through
its vtable - `_Chore` declares exactly one virtual, its destructor, and its vtable here has
exactly one slot to match. `_UnrealizedChore::_Invoke` is an ordinary member calling a
function pointer stored in the object. Rather than guess the offset, the scheduler scans the
first eight words for one that lands in `.text` with the Thumb bit set. It finds it at
**+0x04**, which is what `_Chore`'s published layout implies, but finding it beats assuming it
- assuming an offset is exactly the mistake that had this probe running a destructor as a
work item.

## HandlerType is four words on ARM32, not five

The long-standing "the matching catch has no funclet address" gap. `ehdata.h` guards
`HandlerType::dispFrame` with `_M_X64 || _M_ARM64`, and `_M_ARM_NT` is in neither - so on
ARM32 the structure is `adjectives, dispType, dispCatchObj, dispOfHandler` and the stride is
16 bytes.

Reading it as five words is invisible on the first entry, because the first four fields still
land correctly; every entry after that is one word further out of step. The tell is a catch
whose funclet address is zero and whose catch-object offset holds something that looks like an
RVA - that "offset" is the next entry's handler address, read one field early.

With the stride fixed, the image catches a `LuaException` correctly, through nine cleanup
funclets, having already caught an `IOException` through sixteen earlier in the same run.

## New guards, because none of these failures announced themselves

Three of this round's bugs cost more to find than to fix, and all three for the same reason:
the failure was silent and the report said "budget exhausted". Each now has a detector.

- **Jumping into the heap.** Nothing this probe puts on the heap is code, so executing there
  means the image was handed something that was not a function pointer. Left alone that spends
  the whole budget sliding through zeroed pages, which decode as Thumb no-ops - 22 MB of
  "executed code" in one report was exactly that, and it buried the last real call. Blocks
  deliberately holding hand-assembled code ask for the exemption via `AllocateCode`.
- **Runaway loops.** A loop that neither calls anything nor ends produces no trace at all. One
  million basic blocks with no call across the boundary is far past anything this image does
  legitimately, and hitting it stops the run with the register file.
- **Write watches.** `WatchAllocationsFrom(0x004A24D2)` logs every write into whatever that
  instruction allocates, with the instruction that made each one. Keyed on the *requesting*
  address because the block address is not knowable before the run. This is what finally
  settled the shared_ptr question that three rounds of reading disassembly could not:
  `+0x18` and `+0x1C` were written by one instruction pair, in that order, as a `shared_ptr`.

## The window opens

```
main loop     3 call(s) to CoreDispatcher::ProcessEvents
graphics      device=yes swapchain=yes presents=1 draws=1 clears=2 uploads=21
              viewport=480x800 clear=(1.00,1.00,1.00,1.00)
audio         engine=yes 1 MasteringVoice, 1 SourceVoice
file access   307 opened, 5 failed
FIRST FRAME PRESENTED: cleared to (1.00,1.00,1.00,1.00), 1 draw(s), 1 render target(s) bound
```

Angry Birds Rio reaches its main loop, loads 307 of its own files, builds a GPU pipeline out
of twenty textures, twenty shader resource views, a vertex and pixel shader, an input layout
and four state objects, uploads its art, clears the back buffer to white, draws, and
presents. It also creates an audio engine and a voice.

Nothing is rasterised - this layer records rather than renders - but every call the game
makes to get a frame onto the screen is now made, in order, and answered.

Six defects stood between the last section and this one. Every one of them was silent, and
every one of them presented as something unrelated.

### realloc was not implemented, and Lua would not start

`lua_newstate`'s first act is `realloc(NULL, 0x14C)`. It got the default stub's zero,
returned NULL, and the game threw *"Failed to initialized Lua interpreter"* - four frames and
one C++/CX `__abi_FailFast` away from anything that mentioned memory.

`realloc` on a bump allocator always moves, which is invisible to the caller as long as the
smaller of the two sizes is copied across. Knowing the old size needs no header: the
provenance list already records every block, so `AllocationSizeOf` reads it back.

### Funclets find their frame through r7, not sp and not r11

The runaway from the last section - a `std::vector` destructor walking a `std::string` until
it had destroyed twelve megabytes of heap - was a cleanup funclet computing `this` from a
stale register.

Every cleanup funclet in this image is two instructions:

```
0044B2F4  adds.w r0, r7, #0x38    ; this = frame + 0x38
0044B2F8  b.w    0x40c340         ; tail-call the destructor
```

`r7` is what this compiler's prologues use as the frame base, so `r7` is what its funclets
read. The catch funclet worked only because it happened to restore the whole callee-saved set
on the way in; the cleanup path set the stack pointer and nothing else.

Both now go through one entry point that restores r4-r11 from the frame being unwound and
runs the funclet on a stack below the throw. Which register a funclet reaches through is the
compiler's choice, so the honest thing is to restore all of them and let it use whichever it
was built for.

*(The deep stack matters independently: MSVC's `_CallSettingFrame` runs funclets on the
handler's own stack, and putting sp at the establisher frame instead means every call the
funclet makes allocates its frame on top of the exception object and the locals the remaining
cleanups have yet to destroy.)*

### fseek's offset is signed

`fseek(file, -153652, SEEK_END)` - seek to the start, the long way round. A register holds no
sign, so `Arg` handed back 4,294,813,644, the file pointer landed on 2^32, the next `fread`
returned zero bytes, and the game threw *"Failed to read 4 bytes from
assets/data/fonts/1024x768_wp8/FONT_BASIC.pvr"*.

`CallFrame.SignedArg` exists now, and every parameter declared `int`, `long`, `ptrdiff_t` or
`ssize_t` has to come through it.

### None of the C time functions existed

Seven of them - `time`, `_time64`, `clock`, `_localtime64`, `_localtime64_s`, `_mktime64`,
`strftime` - all answering zero, which is a legal-looking answer for every one: `time`
returns the epoch, `localtime` returns null, and a `struct tm` of zeros is 1 January 1900.
That is exactly what the trace showed while the game formatted a date:

```
formatted  "%g" (r3=0x409DB000) -> "1900"     <- before
formatted  "%g" (r3=0x409FA800) -> "2026"     <- after
```

Time advances but does not track the wall clock: a fixed base plus a counted tick keeps two
runs of the same image comparable, which matters more for a probe than being right about the
date.

### An unknown runtime class now gets a stand-in

Refusing an unregistered class is the *correct* WinRT answer and the image is written to
handle it - vccorlib turns it into a `ClassNotRegisteredException` and a game catches that to
fall back to an offline path. That only works if the exception is delivered, and this probe
cannot deliver a C++/CX throw: the raise stub returns, the caller carries on with a null
factory, and the run ends on a null vtable a few instructions later.

So the choice is not between correct and lenient, it is between stopping at every class not
yet written and carrying on with a stand-in whose every call is logged. `Microsoft.Xbox.User`,
`Microsoft.Xbox.Leaderboards.LeaderboardService` and `Microsoft.Xbox.XboxLIVEService` are all
answered this way, and the report names them as the list of what to implement next.

`Windows.Phone.System.Analytics.HostInformation` got a real implementation rather than a
stand-in, because its one member - `get_PublisherHostId` - has to return the *same* value on
every run. A game that sees a different device each launch treats its own saved state as
someone else's.

### Nothing may throw out of a Unicorn hook

A malformed handler table sent `SearchFrame` reading unmapped memory, and the managed
exception did not unwind to the caller of `EmuStart` - Unicorn calls hooks through a native
callback, so it killed the process and took the entire report with it. Every hook now
converts a host-side failure into a stop, which keeps everything the run had established and
names the stub that failed.

### Audio

`XAudio2Create` is exported by ordinal and by no name, so it appears in the import table as
`XAudio2_8.dll!#1`. The engine, a mastering voice and a source voice now exist.

`IXAudio2Voice` is why this is a separate file from the Direct3D layer rather than more of the
same machinery: it derives from **nothing**. No QueryInterface, no AddRef, no Release - its
vtable starts directly at `GetVoiceDetails`, and a builder that reserves the first three slots
for `IUnknown` would put every voice method three places out. The two methods that hand data
back, `GetVoiceDetails` and `GetState`, are answered as a voice with nothing queued and
nothing played, because a voice reporting buffers still queued is never asked for more.

## A thousand frames

```
main loop     1020 call(s) to CoreDispatcher::ProcessEvents
graphics      presents=1018 draws=1018 clears=1018 maps=2036 uploads=1056
audio         engine=yes 1 MasteringVoice, 1 SourceVoice
file access   739 opened, 5 failed
elapsed       53s
```

One number did that, and it was an off-by-one in an argument index.

### Map takes six arguments, not five

`ID3D11DeviceContext::Map(pResource, Subresource, MapType, MapFlags, pMappedSubresource)` is
five parameters - and six with `this`. The out-parameter is therefore the **second** stack
slot, not the first. Reading the first gave `MapFlags`, which is zero, which is
indistinguishable from a caller passing no out-parameter at all: every `Map` answered
`E_FAIL`, and the report agreed that nothing had been mapped.

The image did not check. It took its no-mapped-buffer path, and a vertex copy several frames
later ran off the end of a sixteen-byte vector using a destination that path had never
initialised. That was the previous section's unexplained overflow: not a bad number in a mesh
loader, a bad number in this file.

Two more of the same kind were sitting next to it. Every `Create*Shader` takes
`pShaderBytecode, BytecodeLength, pClassLinkage` and then its out-parameter, so the
out-parameter is index **4** with `this` counted. Index 3 is `pClassLinkage`, which is
almost always null - so the creator saw a null out-pointer, wrote nothing, and returned S_OK.
The image kept whatever its own memory already held where the shader pointer belonged, and
drew with it.

**The rule, since this is now the third time an out-parameter has done real damage:** count
`this`. A COM method's first parameter is index 1, and a method with four declared parameters
puts its last one at index 4, on the stack. Getting that wrong does not fail - it reads the
neighbouring argument, which is usually a flag, which is usually zero, which looks exactly
like the caller not asking for anything.

### Watching a write is not the same as refusing it

The trap page had 212 damaged slots while the report cheerfully listed 1,356 writes to it as
"refused". They were not refused. `UC_HOOK_MEM_WRITE` fires *after* the store lands and
cannot veto it - the hook was a witness, not a guard, and the only thing genuinely refusing
anything was the host-side check in `WriteMemory`, which the emulated CPU does not go
through.

The trap page is now mapped **read and execute, but not write**. Our own writes still work,
because `uc_mem_write` bypasses page protection - exactly the asymmetry this needs. An
emulated store into it now faults and stops the run with the instruction and the register
file, instead of quietly turning an import into a jump to wherever a register points.

## Instruction traces

`WPR_TRACE=0x00534E3A,0x00462494` logs the register file every time those instructions run.

A watch says what a piece of memory became; a trace says what a register held when a
particular instruction ran. The second question is the one that comes up once the image is
deep enough that its own arithmetic is the suspect: a destination computed as
`base + stride * index` is three values, and the only way to tell which is wrong is to look
at them. Here it was both - base zero and stride 0x70000000 - which is what pointed at a
structure that had been zeroed rather than at a number that had been miscalculated.

## The game runs

```
main loop     63182 call(s) to CoreDispatcher::ProcessEvents
graphics      presents=63180 draws=248225 clears=63181 maps=496450 uploads=248300
file access   824 opened, 5 failed
elapsed       300s
```

Sixty-three thousand frames, four draw calls each. It is no longer a loading screen with one
quad on it - the game is drawing a scene, loading as it goes, and doing it for as long as the
instruction budget allows.

### calloc takes two arguments

`calloc` was in the same table as `malloc`, `operator new`, `operator new[]` and
`Platform::Heap::Allocate`, all pointing at the same one-argument handler. It is the only
member of that family that does not take a single size, so `calloc(1, 0x724)` - a 1,828-byte
object - read `nmemb` as the size, rounded 1 up to the allocator's sixteen-byte minimum, and
handed back sixteen bytes.

The image then used that object normally. Its texture decoder keeps three plane pointers and
a row pitch at offsets `0x6A4` through `0x6B0`, which is to say **1,700 bytes into a
sixteen-byte block** - in whatever happened to be allocated next. Reading them back gave a
null base and a stride of `0x70000000`, and the decoder wrote 16-byte texture blocks at
`base + stride * index` straight into the trap page.

`_calloc_crt`, three lines further down the same table, had been right all along. That is
what made it invisible: the wrong one looked like the others around it.

## An allocator that frees

The bump allocator never freed, and while a run ended at the first fault that cost nothing.
A running game is different: this one turns over about 180 KB a frame, so a quarter of a
gigabyte bought roughly fourteen hundred frames and then the run ended for a reason that had
nothing to do with the image.

Three changes, in order of how much they mattered:

- **`free`, `operator delete`, `Platform::Heap::Free` and `CoTaskMemFree` now free.** Blocks
  go on an exact-size free list; `realloc` frees the block it just copied out of. Freeing the
  same block twice is ignored rather than putting one address in a bucket twice, which would
  turn an image bug into an emulator one.
- **Size buckets.** Exact-size reuse only helps when the same size comes back, and a game
  asking for 1,500 bytes and then 1,504 gets nothing from having freed the first. Sixteen-byte
  granularity below a kilobyte, powers of two above it. Reuse went from 116 MB to 179 MB over
  the same work, and the heap high-water mark from 233 MB to 52 MB.
- **The heap is a gigabyte**, with the trap page and TEB moved above it. The ceiling is gone
  rather than raised.

There is one honest cost. The overflow guard reads its room from the *bucketed* size, so a
large block now carries slack it did not ask for and a small overrun past it goes unnoticed.
That is the same slack a real allocator's size classes give; overruns of the size that have
actually mattered here - 144 bytes into 16 - are still caught.

Splitting and coalescing are deliberately absent. Exact-size-with-buckets recovers nearly
everything for about fifteen lines, and a splitting allocator brings every classic allocator
bug with it, in a component whose whole job is to make the *image's* bugs visible rather than
to add its own.

### The block list is searched, not scanned

`DescribeAllocation` and `AllocationSizeOf` walked the whole allocation list, which was fine
when a run made a few thousand allocations and stopped. The overflow guard put
`AllocationSizeOf` on the path of **every host write** - a linear scan of a hundred thousand
blocks per `memcpy` is not a diagnostic, it is a different program. Both binary-search now.
The list stays ordered because the bump allocator only ever hands out increasing addresses,
and a reused block keeps its original place in it.


## Input, and a game that can be touched

The image subscribes to five CoreWindow events during `SetWindow` and then waits. Accepting
the subscription and never raising it is honest but sterile: a game that is never touched
stays on its title screen forever, and everything behind that screen is unreachable no matter
how much of the platform works.

`CoreDispatcher::ProcessEvents` is where a real dispatcher delivers input, so it is where this
does too - one tap, at the middle of the screen, after 240 frames, held for eight. Late enough
that the image has finished whatever it does on its first frames, and held because a game that
samples input once a frame can miss a press and release delivered back to back.

```
input  PointerPressed at (400, 240) -> handler 0x60003110 invoke 0x00445DA5
```

## The tap works, and the game moves

```
main loop     18,688 dispatcher turns
graphics      18,686 frames presented, 73,400 draws
input         124 taps delivered, every handler returning S_OK
file access   779 opened
```

`rovio-splash.png` is frame 200. `fox-splash.png` is frame 700, after one tap. `title-screen.png`
is frame 1500, after a few more: Blu and Jewel on the branch with the wordmark behind them -
the Angry Birds Rio title screen.

Two reference-counting bugs stood between "a tap crashes" and this, and the second only
appeared because the first was fixed.

### Accepting a callback is a promise to keep it alive

`ThreadPool.RunAsync` recorded the handler's function pointer and returned. It never took a
reference on the delegate - and the caller is entitled to release it the moment RunAsync
returns, which this image does immediately.

That ran on luck for a long time. The luck ended the moment weak references started working:
the release path actually destroyed the captured functor, and the drain then invoked a
delegate whose vptr had already been walked back to `__abi_CaptureBase` - so slot 1 was the
next thing along in `.rdata`, and the CPU jumped into it. The symptom was an invalid
instruction during startup with no visible connection to refcounting at all.

The same omission was in every `add_SomeEvent` on the discovery objects, and it produced a
better clue: all five CoreWindow subscriptions came back with the **same handler address**,
because each delegate was freed before the next was allocated. A "handler" whose first three
words pointed at itself is not a delegate, and that is what it looked like.

Both now take a reference. `KeepAlive` does it the only way a host stub can - AddRef is
emulated code, so it tail-calls, and the return trap puts S_OK back in r0 and continues to
wherever the stub was going to return.

**The allocator is what exposed this.** While it never freed, a missing AddRef cost nothing;
the memory stayed valid because nothing could reuse it. Teaching it to recycle turned a latent
correctness bug into a visible one, which is the point of making an allocator behave like a
real one even in a probe.

### The marshaler, resolved

`CoCreateFreeThreadedMarshaler` now returns **S_OK with a null marshaler**. Three earlier
attempts handed back an object - a bare stand-in, one with strict IID matching, and a properly
aggregated inner unknown that delegates to the controlling unknown - and all three died the
same way, because the image calls a method on whatever it is given.

Success-with-null is a lie about a pointer rather than about a vtable, and that is the whole
difference: nothing can be called on null, so if the image ever does use the marshaler it is a
null call at the exact instruction that used it. It never does.

What it buys is the weak reference. The caller stores one, calls this, and **on failure
releases the weak reference and nulls it** - so refusing did not disable marshalling, it
disabled weak references for the whole image, and every event handler ended up holding null.

### One tap is not enough

A game does not open on the screen anyone wants to see. This one shows the publisher, then the
licensor, then a title screen, and each waits for a touch. `WPR_TAP=150` taps every 150
dispatcher turns rather than once; 124 taps is what it took to get the picture above.


## Where it stops now

Two different places, depending on whether the tap is delivered.

Left alone, the game runs until the instruction budget does:

```
main loop     63182 call(s) to CoreDispatcher::ProcessEvents
graphics      presents=63180 draws=248225 clears=63181
```

Tapped, it stops in its own pointer handler at `0x0044F0EF`, resolving a null weak reference.

Neither is a wall in the way the earlier ones were. The first is a budget; the second is one
more object this probe has not built yet.


## Launching it on the desktop

`Desktop/WPR.Wp8Desktop` is the same emulator in a window. It compiles the probe's sources
directly - nothing in them ever depended on being a console app - adds a WinForms `Form`, and
meets the emulator at exactly two points: every presented frame comes across as a private RGBA
copy through `Direct3DRuntime.FramePresented`, and the mouse goes back through
`WinRtRuntime.InjectPointer`, which queues it for the image's next turn round its own main
loop. Two threads, and nothing touches the CPU from the window's.

```powershell
.\run.ps1 -Desktop -Game C:\wp8-test\abrio\AngryBirdsRio.exe
```

Verified 2026-09-02: Angry Birds Rio boots to the Fox splash in a 1200x720 window, a click on
the client area is delivered as press-and-release, and it advances to the title screen and runs
there at **40 fps**. The window's title bar carries the frame count and rate; a `0 fps` during
the first half-minute is the load phase, not a stall - frames there cost tens of millions of
instructions each.

Two things it is for that the console probe is not. A person can **tap wherever they like**:
every scripted run so far tapped the centre of the screen, and a title waiting for a touch on a
button somewhere else would have looked exactly like one waiting for nothing. And it is the
shape a real backend has - a surface and an input source, with the emulator behind them - so
what is learned here about threading and lifetime carries over.

Two things it is not. The frame is still the software rasteriser, so anything with shaders is
still wrong; and the scripted taps are **off** in this host (`WPR_TAP=0` is set before the
runtime is touched), because a real pointer replaces the script.

### The first thing it appeared to settle, and did not

With the title screen up, fifteen clicks were posted across the whole client area and every
frame hash after them was identical - and the conclusion drawn was that nothing on that screen
responds to a touch. That conclusion was wrong, for a reason recorded under *Taps* below: every
one of those taps was delivered with its position corrupted, so this experiment tested nothing.
It is left here because it is a clean example of the trap - a negative result from a tool is
only as good as the tool, and this one had not yet been proved on a positive.

## A picture

`WPR_SCREENSHOT=path[:frame]` rasterises one presented frame and writes it as a PNG.
`rovio-splash.png`, `fox-splash.png` and `title-screen.png` are frames of Angry Birds Rio,
drawn from the game's own vertex buffers and its own decoded texture atlases: the two
publisher splashes, and then the title screen - the full wordmark, the Rio landscape, Blu and
Red on the rock, and the word LOADING.

Getting there needed the Direct3D layer to stop throwing away everything it was handed.

### Resources with something in them

Answering a Create call with a shaped object was enough to keep the image running, and it was
all this layer did. `Map` gave everybody the same scratch buffer, so the vertices one draw
wrote were overwritten before the next draw read them; `UpdateSubresource` counted its calls
and discarded the pixels; and the art the game spent its entire startup decoding went
nowhere. Nothing could be drawn from that.

Every resource now owns emulated memory the size of its descriptor, and every path that fills
one - `pInitialData`, `Map`, `UpdateSubresource` - fills that. `CreateInputLayout` keeps its
element descriptions, `CreateShaderResourceView` remembers which resource it views, and the
bind calls record what is bound. A draw records all of it.

### The rasteriser

`FrameCapture` walks the recorded draws at Present and fills triangles into an 800x480 image
(see *The window is portrait and the game is not* below for why that is not the device size):
barycentric coverage, nearest sampling, source-alpha blending, and a hand-rolled PNG writer
using stored deflate blocks, because pulling in an image library for a diagnostic would be
the tail wagging the dog.

It runs no shaders. The position is read from the vertex as the input layout describes it and
transformed by the first constant buffer read as a 4x4 matrix, which is what a 2D engine's
vertex shader does and nothing more. That is why this is worth anything for this title and
would be worth nothing for a 3D one.

### Two mistakes, and what each looked like

Both are worth recording, because neither looked like what it was.

**The transform is row-major.** HLSL packs a `float4x4` constant column-major by default and
the shader does `mul(position, matrix)` with a row vector, which comes to a dot product with
each *row* of the buffer. Doing it the other way transposes the transform - and for a 2D
orthographic projection the matrix is diagonal, so transposing it changes **nothing at all**.
The first two screenshots were byte-identical and it took noticing that to realise the bug
was elsewhere.

**A vertex can come from more than one buffer.** The layout reported `POSITION@0` and
`TEXCOORD@0` - both at element offset zero, in a twelve-byte vertex, which is impossible
until you look at `InputSlot`: position comes from slot 0 with a 12-byte stride, texture
coordinate from slot 1 with an 8-byte stride. Keeping only the first buffer meant every
texture coordinate was read out of the position, so every quad sampled the atlas at its own
screen position.

That produced a picture rather than nothing, which is what made it interesting: the Rovio
logo appeared, recognisable, mirrored, in the wrong quadrant and the wrong size. A wrong
answer that looks almost right is the expensive kind, and a rasteriser is unusually good at
producing them - the output is a picture either way, and a picture is very easy to accept.

### Geometry belongs to the draw, not to the frame

The recorded draws were walked at Present, and the vertex buffers were read *then*. That is
one buffer too late. This engine draws a frame by mapping a single dynamic vertex buffer,
writing a quad, drawing it, and rewriting the same buffer for the next quad - so by Present
every draw in the frame resolves to the last quad's geometry.

The symptom was not an empty screen, which is the trouble with it: three different textures
were drawn in exactly the same place at exactly the same size, stacked on top of each other,
with the rest of the screen left as the clear colour. It reads as "the game only drew one
thing", not as "the geometry is stale".

`FrameCapture.Snapshot` now copies the indices, and the slice of each vertex stream those
indices actually touch, at the moment the draw is issued. Nothing the game does afterwards
can change what a recorded draw meant. `ReadElement` reads from that copy rather than from
emulated memory, which also removed the emulator argument from four functions.

### Two ways to read a texture wrong

**Channel order is not a constant.** Sampling assumed B8G8R8A8, because that is what the swap
chain is. This title's art is R8G8B8A8 - format 28, not 87 - so red and blue were swapped
everywhere. Bad enough to see once pointed out and easy to miss until then: the sky stayed
plausible, the landscape stayed plausible, and the tell was a red bird coming out blue.
Sampling now reads the channel order out of the resource's own format.

**A block-compressed texture has no pixel rows.** The sprite atlas is BC3 (format 77), where
each 4x4 pixel block is sixteen bytes, `RowPitch` is a row of *blocks*, and the row count is
in block rows. Reading that as four bytes per pixel takes a quarter of the data at four times
the stride and produces torn noise - which does not look like a format mistake, it looks like
a corrupt upload, and it sent me looking at `UpdateSubresource` first.

`DecodeBlocks` expands BC1, BC2 and BC3 to RGBA once, on load, and stamps the decoded copy as
format 28 so the sampler above needs to know nothing about it. The colour half is the same in
all three - two RGB565 endpoints and sixteen two-bit selectors - and only the alpha differs:
BC1 takes it from the endpoint ordering, BC2 carries four bits per pixel, BC3 carries two
endpoints and sixteen three-bit selectors of its own.

### The window is portrait and the game is not

The last one was not in the rasteriser at all. Every sprite was drawn 1.67x too wide: the
title wordmark ran off both edges of the screen and only the middle of it was visible.

The obvious suspect is the projection, so the projection is where the time went. It is not
there - the constant buffer holds two identity matrices and a rotation for something that
spins, and no projection at all, at any slot, on any draw. The vertices arrive in clip space
already.

The measurement that settled it: for a sprite drawn at 1:1, the on-screen size in pixels has
to equal the size of the texture region its texture coordinates select. Four draws agreed on
the same answer, to within a pixel - and the size they agreed on was **the transpose** of the
size the image had been told the device was.

That is Windows Phone 8 working as designed. The CoreWindow is always in the device's native
**portrait** orientation, 480x800 on WVGA, and a landscape game swaps the axes itself. Being
told 800x480, this image dutifully swapped to a 480x800 layout - and laid the landscape art
out inside it.

Because it does pick the art from that size. Told 480x800 it loads a 480-wide title wordmark;
told 800x480 it loads a 767-wide one. So the wrong device size did not merely scale things
wrongly, it loaded a *different asset set* and then laid that out for the wrong viewport,
which is why the error was 1.67x rather than something that looked like a mistake in a matrix.

There are now two sizes, deliberately kept apart:

| | what it is | default | override |
| --- | --- | --- | --- |
| `Direct3DRuntime.BackBufferWidth/Height` | the device: swap chain, and the window bounds that go with it | 480x800 | `WPR_WINDOW=WxH` |
| `FrameCapture.Width/Height` | the surface draws are rasterised into | 800x480 | `WPR_SCREEN=WxH` |

They are transposes of each other and that is correct, not a leftover. The game composes in
landscape and presents into a portrait buffer, applying the rotation in its vertex shader -
so a layer that runs no shaders sees the landscape composition, which is the one worth
looking at anyway. With this the geometry lands texel-exact: a 480x219 wordmark drawn at
478x162 pixels, a 17x35 sprite at 17x35.


## printf

`sprintf` returning 0 and writing nothing was what made the image format a string, parse
back an empty one, and throw. It is implemented now, in two pieces.

**`VarArgReader`** walks the variadic arguments. Under AAPCS every argument takes the next
slot in one sequence — core positions 0-3 are r0-r3, position 4 onwards is the stack at
`[sp]`, `[sp+4]` — and at the moment a trap fires sp still points where the caller left
it, so the stack half needs no adjustment.

The part that catches people out is floating point: a variadic `double` is **not** passed
in a VFP register. It goes in a *pair of core registers*, and the pair must start at an
even position. So `printf("%d %f", 1, 2.0)` puts the int in r2, **skips r3**, and passes
the double on the stack. Read that wrong and you get half an argument and everything after
it shifted.

**`PrintfFormatter`** interprets the format string: flags, width and precision (including
`*`), length modifiers, and the `diouxXeEfFgGcspn%` conversions, in the C locale. Two
details worth having got right — `.NET` writes a three-digit exponent where C writes two
(`e+006` vs `e+06`), which matters to anything parsing the output back; and an explicit
precision defeats the `0` flag.

It works on the image's own calls:

```
"%%g" (r2=0x00000000 r3=0x70000719) -> "%g"
"%g"  (r2=0x00000000 r3=0x00000000) -> "0"
```

The first is a literal `%g` — the image builds its number format string at runtime — and
the second uses it. Both correct, including the double arriving in the r2:r3 pair.

`sscanf` and `strftime` are still absent; scanning a string back into caller-supplied
pointers is a separate job from formatting one.

### It still throws, and that is now the whole story

Formatting was not the reason. After the two calls above the image runs
`strlen`, `memcmp`, `isdigit`, allocates, and throws — a formatted value being validated,
or a parser using exceptions for control flow, which C++ code does routinely. Either way
the next blocker is not a missing function: it is **exception unwinding**. The `.pdata`
tables are all present in the image and nothing reads them yet.

## Exception unwinding — the walk works

`ArmUnwinder` recovers the call stack at an arbitrary PC from the image's own `.pdata`
table. At the throw it now produces this:

```
stack at the throw (7,671 .pdata entries: 3,554 packed, 4,117 xdata):
  0x004643D4  in function 0x00064199  frame 1624 bytes  xdata  [has handler]
  0x00464876  in function 0x00064809  frame 1128 bytes  xdata  [has handler]
  0x00466068  in function 0x00066029  frame   64 bytes  xdata  [has handler]
  0x004660D0  in function 0x00066091  frame   48 bytes  xdata  [has handler]
  0x0044A0C4  in function 0x0004A081  frame  320 bytes  xdata  [has handler]
  0x0044A5DE  in function 0x0004A3D1  frame  152 bytes  xdata  [has handler]
  0x00444208  in function 0x000441E9  frame   40 bytes  xdata  [has handler]
  0x00444834  in function 0x00044815  frame   40 bytes  xdata  [has handler]
  0x70000CA4  walk stopped: return address is not in executable code
```

Eight frames, every one carrying a handler, with plausible frame sizes.

**Three things say it is right.** The frame sizes are sane rather than wild. The innermost
frame `0x004643D4` sits inside the function `.pdata` names (`0x464198`–`0x4643EE`), and so
does `0x004643B8` from the basic-block trail, which is recorded by an entirely separate
mechanism. And the walk stops at `0x70000CA4` — an address in the **trap page**, which is
a `CallEmulated` return trap. The stack it walked began with a host-to-emulated call, so
ending exactly at the host boundary is a structural prediction that came true.

### What was wrong before

The unwind codes are not a description of a frame's shape. They are a small instruction
set that has to be **executed** against real machine state, and one opcode makes that
unavoidable:

| code | what it actually is | what I had guessed |
| --- | --- | --- |
| `C0-CF` | **`mov sp,rX`** — restore sp from a register | a VFP push |
| `D8-DF` | `pop {r4-rX,lr}`, X = (code & 3) + **8** | +4, so four registers short |
| `E0-E7` | **`vpop {d8-dX}`**, 8 bytes each | an integer pop |
| `F8` / `F9` | 3-byte and 2-byte operands | swapped |

`mov sp,rX` is the one that matters. A function using a frame pointer — and therefore
possibly `alloca` — does not have a frame of computable size; its caller's stack pointer
comes out of a register. Every function in this stack starts its unwind with `C7`, i.e.
`mov sp,r7`. No amount of adding up frame sizes recovers that.

So the interpreter runs against a register file that starts as the live CPU state and is
updated as registers are popped, which also makes each successive frame's registers
correct — exactly how a real unwinder works.

## The handler search

`CxxExceptionModel` reads the structures MSVC emits for C++ exceptions and answers the two
questions a real handler search asks: what is being thrown, and who catches it.

**What is thrown.** The `ThrowInfo` passed to `_CxxThrowException` names every type the
object can be caught as — the class and all its bases, which is what makes
`catch (const std::exception&)` catch a derived type:

```
thrown: .?AVIOException@io@@  (object at 0x502FF498)
  also catchable as .?AVException@lang@@
  also catchable as .?AVThrowable@lang@@
  also catchable as .?AVexception@std@@
```

**That is the answer to the whole mystery.** It is an `io::IOException` — a *file* error.
The image is trying to read something, and there is no file I/O implemented, so it throws.
Every turn spent chasing the throw as if it were a formatting or parsing bug was chasing a
symptom. (Note also the shape of that hierarchy: `lang::Throwable` → `lang::Exception` →
`io::IOException` is Java's, which says something about where this engine was ported from.)

**Who catches it.** Each frame's handler data is a `FuncInfo` listing try blocks, the range
of EH states each covers, and the catch clauses on them. Mapping the frame's PC through the
IP-to-state table gives the active state, and any try block covering it is a candidate:

```
0x004643D4  FuncInfo: no try blocks, cleanup only (state 11 of 16 ip entries)
0x00464876  FuncInfo: no try blocks, cleanup only (state 2 of 7 ip entries)
0x00466068  FuncInfo: no try blocks, cleanup only (state 1 of 5 ip entries)
0x004660D0  FuncInfo: no try blocks, cleanup only (state 1 of 5 ip entries)
0x0044A0C4  FuncInfo: 1 try block(s), state 1 of 32 ip entries
0x0044A5DE  FuncInfo: 1 try block(s), state 13 of 29 ip entries
...
-> catch(...) at funclet 0x0004A379 in function 0x0004A081 (states 0-13)
```

The distinction in that listing matters: a frame with handler data but **no try blocks** is
registered for destructors, not catching. Four of the eight are exactly that, which is why
"has a handler" was never the same question as "catches this".

**The image expects this failure.** There is a `catch(...)` waiting three frames out, so
the missing file is a case it handles rather than a crash. That reframes what is left to
do: transferring into that handler would let it carry on, with or without file I/O.

### One bug worth recording

Every `FuncInfo` initially read as invalid — `magic 0x19930522` reported as "not a
FuncInfo", while `0x19930522` was sitting in the table of accepted values. The check was
`magic & 0xFFFFFFF`: seven Fs, not eight, silently masking off the top nibble.

## File I/O

`FileLibrary` backs the image's file access with real files, over two roots: the unpacked
package, read-only, holding everything shipped with the game; and a writable sandbox in the
temp directory standing in for the app's local folder. Nothing the image writes escapes the
sandbox.

Both layers are implemented, because the image uses both — C stdio (`fopen`, `_wfopen`,
`fread`, `fwrite`, `fseek`, `ftell`, `feof`, `fprintf`, `_read`, `_lseek`, `_fileno`) for
assets, and Win32 (`CreateFile2`, `GetFileAttributesExW`, `CreateDirectoryW`,
`MoveFileExW`, `CloseHandle`) for save data.

Path handling has three wrinkles worth knowing:

```
"assets\FusionXbox.json"                     -> 1819 bytes
"ASSETS\DATA\SHADERS\DX11\2d-sprite.fxo"     -> 5644 bytes   (wrong case)
"assets/data/scripts/Bank.lua"               ->  880 bytes
"C:\Applications\Install\WMAppManifest.xml"  -> 4507 bytes   (install path)
"assets/data/does-not-exist.bin"             -> NOT FOUND
```

- **Case.** WP8 paths are case-insensitive and the image is written accordingly, but this
  probe runs on a case-sensitive filesystem. Every lookup falls back to a case-insensitive
  walk, one segment at a time.
- **Install paths.** A package lives at `C:\Applications\Install\{ProductId}\Install\...`,
  so anything after the last `Install` segment is the path within the package.
- **Writes.** Anything under the local folder prefix goes to the sandbox. So does anything
  the image tries to create that is not shipped in the package.

### What it opened first

```
fopen("C:/Data/Users/.../Local/devconfig.json", "rb") -> not found
```

`devconfig.json` — an optional developer config, in the local folder, which genuinely does
not exist. That is what the `io::IOException` is about, and the `catch(...)` three frames
out is the image handling exactly this case. **Nothing here is broken.** The file is
supposed to be missing; the image is supposed to throw; and it is supposed to carry on.

Creating an empty one to dodge the throw would be the wrong fix — it would substitute a
parse failure for a missing-file case the image already handles, and hide the fact that
the transfer is what is missing.

### A note on the extracted package

While testing this, most of the unpacked assets vanished from the scratchpad — temp
cleanup pruned everything that had not been read recently, leaving three files and the
directory structure. The XAP itself was untouched; re-extracting restored all 1,430 files.
Worth knowing before concluding that a resolution failure is a bug in the resolver.

## The transfer — the image now catches its own exception

A catch clause is compiled as a **funclet**: a separate function that shares the
establisher frame's locals. Entering one means putting the stack pointer back where that
frame had it, copying the caught object into the frame slot the funclet expects, and
calling it. It returns the address to resume at — the instruction after the whole
try/catch.

Before that, the destructors. Each frame's unwind map is a chain: state N names a funclet
to run and the state to move to next. Walking it from the frame's current state down to
the try block's `tryLow` runs the destructors for every scope being abandoned. Sixteen of
them run here, across three frames, and they are visible in the import trace as the eight
`operator delete` calls between the throw and the resumption:

```
651. _CxxThrowException          <- the throw
652-659. operator delete  x8     <- destructors, from the cleanup funclets
660. QueryPerformanceFrequency   <- code after the catch
661. QueryPerformanceCounter
```

And the transfer itself:

```
entering catch(...) funclet 0x0004A379 with frame 0x502FFD18, after 16 cleanup funclet(s)
   cleanup state 11 in function 0x00064199 -> funclet 0x00064447
   cleanup state 10 in function 0x00064199 -> funclet 0x0006443F
   ...
   funclet returned continuation 0x0044A36D

last blocks  0x70000CE8 -> 0x0044A36C -> 0x00580238 -> 0x0058024E -> 0x0044A374 -> ...
```

`0x0044A36C` is that continuation, and execution carries on from it into code it had never
reached before. **The image throws, unwinds, runs its destructors, catches, and continues.**

The catch funclet ABI is not in the ARM exception handling specification — it is an MSVC
CRT internal — so it was implemented from how `_CallSettingFrame` behaves: r1 carries the
establisher frame, and the funclet returns the continuation address in r0. The evidence
that it is right is that the returned address is a valid Thumb code address inside the
function that owns the try block, and that execution runs on sensibly from it.

## Data imports — the bug behind the null call

A DLL exports data as well as code, and the CRT does plenty of it: `_fmode` and
`_commode` are ints, `_acmdln` is a string pointer, `_HUGE` and `_FInf` are floating-point
constants. Their IAT slots hold **the address of a variable**, and startup code writes
*through* them.

The loader pointed every IAT slot at a trap, those five included. So the CRT stored
through what it thought was a variable and landed in the trap page — the one region that
holds the emulator's own instructions. A four-byte store through `_fmode` at an unaligned
address clipped the low byte of a neighbouring trap:

```
trap slot 0x700008D0 holds  00 47      <- 0x4700 = "bx r0"
should hold                 60 47      <- 0x4760 = "bx r12"
```

The next call through that import — `QueryPerformanceCounter` — arrived, ran its host
handler, and then left through `bx r0` with r0 holding the handler's return value of 1.
Hence a jump to address 0, hundreds of instructions and one whole exception later, with
nothing connecting it back.

The fix is to give a data import a real variable instead of a trap. With that,
execution runs straight through the epilogue that used to fail:

```
0x0044A692 -> 0x0044A698 -> 0x00580238 -> 0x0058024E -> 0x0044A6A0 -> 0x00444208
                            ^ /GS cookie check          ^ pop {...,pc}   ^ the caller
```

### Three guards, so this class of bug cannot hide again

- **Host writes are refused** if they target the trap page. A stub writes through pointers
  the image supplies, and the image can supply a bad one.
- **Emulated writes to the trap page are watched** and reported with the PC that made them.
  Legitimate traffic there is zero, so the hook costs nothing.
- **The trap page is verified** before and after every run, slot by slot. A single altered
  byte there is catastrophic and almost untraceable; checking it is one pass over a few
  hundred slots.

### What I got wrong

The previous write-up concluded this was a "four-byte frame-size error" — that the
unwinder made function `0x4A3D1`'s frame 152 bytes while its epilogue accounted for 148.
That was wrong. The epilogue's `bl` calls the /GS cookie helper, and **that helper adjusts
the stack too**:

```
0x00580220  sub sp, #4      <- prologue helper allocates the cookie slot
0x0058024E  add sp, #4      <- epilogue helper releases it
```

116 + 4 + 32 = 152. The unwinder was right all along; the arithmetic that looked like a
missing word was a missing instruction in my reading of it.

## How the null call was originally found

The run ends reporting a null call "from `0x0044A693`". Disassembling the site shows that
address is not the culprit:

```
0x0044A68A  add.w r0, r8, #0x50
0x0044A68E  ldr   r3, [r3]        ; r3 = *(0x005854C0)
0x0044A690  blx   r3              ; <- the reported address
0x0044A692  b     #0x44a698
...
0x0044A698  mov   r0, r8
0x0044A69A  add   sp, #0x74
0x0044A69C  bl    #0x580238
0x0044A6A0  pop.w {r4, r5, r6, r7, r8, sb, fp, pc}   ; <- the real failure
```

IAT slot `0x005854C0` is `QueryPerformanceCounter`, and it was called **successfully** —
it is entry 661 in the import trace. The address in the report is a stale `lr` left by that
call. The jump to zero actually comes from the epilogue four instructions later: the
function pops its own return address off the stack and gets a zero.

So this is not a missing API. The cause turned out to be data imports - see the section
above. The disassembler added here (`disarm.py`, capstone over the same RVA mapping) is
what made it findable, together with a write watch on the trap page.

### A second defect, confirmed

The third catch candidate reports `funclet 0x00000000` and `catchObj=+0x44857`. Both are
nonsense — `0x44857` is an RVA, not a frame offset — which confirms the `HandlerType`
stride is wrong for entries past the first in a handler array. It did not matter here,
because the chosen catch is the first entry of its array, but it will.

One real bug turned up while looking at it, though it was not the cause:
`QueryPerformanceFrequency` and `QueryPerformanceCounter` were sharing a handler, so the
image was told its clock ticked at whatever the current time happened to be. The frequency
is now a frequency.

## Layout

| file | role |
| --- | --- |
| `PeImage.cs` | PE32 reader: sections, entry point, and every imported function with its IAT slot |
| `ArmEmulator.cs` | Unicorn setup, address-space layout, trap table, lazy page mapping |
| `HostStubs.cs` | Host implementations of trapped **imports**: timing, allocation, memory, HSTRING |
| `CrtLibrary.cs` | The C runtime: character, string, number and Concurrency functions |
| `CallFrame.cs` | Argument and return-value plumbing for the ARM calling convention |
| `VarArgReader.cs` | Variadic arguments across registers and stack, with the 8-byte alignment rule |
| `PrintfFormatter.cs` | The C format-string interpreter |
| `ArmUnwinder.cs` | .pdata indexing and unwind-code execution — the stack walk |
| `CxxExceptionModel.cs` | ThrowInfo, FuncInfo and the catch search |
| `FileLibrary.cs` | stdio and Win32 file I/O over a read-only package and a write sandbox |
| `HStringHeap.cs` | WinRT string allocation and reading, shared by the stubs and the objects |
| `WinRtRuntime.cs` | Synthesised COM objects and vtables, storage, and the view lifecycle driver |
| `VtableProof.cs` | Hand-assembled Thumb-2 exercising both bridges and the lifecycle |
| `Program.cs` | The probe driver: image summary, import census, run, bridge check, throughput |

Only `Program.cs` and `VtableProof.cs` touch the console; the rest lift out of here
unchanged if this graduates into a backend project.

`Desktop/` is the WinForms host - see *Launching it on the desktop*. It is excluded from the
probe's own compile glob (`<Compile Remove="Desktop\**" />`) so the console probe stays
buildable under WSL, and it compiles the probe's sources by `<Compile Include="..\*.cs" />`
rather than referencing the exe.


## Input is a script, not a tap

Press-and-release at the centre of the screen gets past a splash. It cannot fire a bird,
because a slingshot is a drag - and a drag is not a tap with extra steps, it is a different
event stream with a position that changes and a clock that has to be believable.

`WPR_INPUT` is a semicolon-separated gesture script, cycled one gesture per `WPR_TAP` turns:

| gesture | what it delivers |
| --- | --- |
| `tap` / `tap:x,y` | press, eight moves at the same point, release |
| `drag:x1,y1>x2,y2` | press, twelve interpolated moves, release |
| `drag:x1,y1>x2,y2@n` | the same with `n` moves |
| `wait:n` | spends `n` turns round the main loop touching nothing |

Coordinates are in the **landscape space the image composes in**, not the portrait bounds the
device reports - see *The window is portrait and the game is not*.

**Waiting is a gesture, which is what makes a script a timeline.** Gestures start on a period
boundary, so without `wait` the only way to put two hundred frames between two taps is a period
of two hundred frames - which then also delays the first tap by two hundred. With it, a period
of 60 and `wait:1150;tap:624,360;wait:120;tap:400,288` means: let it run to the menu, dismiss
the dialog it opens, let that animate away, then press PLAY. That is the shape every screen
past the first one needs.

**One event per turn round the main loop, and that is not a throttle.** Delivering an event
is a tail call into emulated code that takes over the return path, so a stub physically
cannot deliver a second one and still return. A twelve-step drag therefore takes twelve
frames, which at the rate this runs is about a third of a second - close enough to a real
drag that nothing has to pretend.

Three things had to be true before a drag meant anything:

- **PointerMoved had to actually be subscribed.** It was - all five CoreWindow subscriptions
  are - but nothing reported that, so it was an assumption. It is now printed with the run:
  `5 CoreWindow subscription(s); PointerPressed, PointerMoved, PointerReleased`.
- **get_Position had to move.** It was a pair of constants. A drag whose position never
  changes is a long press.
- **get_Timestamp had to exist.** `IPointerPoint` slot 9 was reaching the discovery default
  on every single pointer event, which answers an out-parameter with a placeholder object
  pointer - so the image was reading a pointer as a microsecond count. A flick, a swipe and a
  slingshot are all a position difference over a time difference, so this is the value that
  decides how hard the bird is thrown. It is now the frame counter at a notional 60Hz, rather
  than the tick this probe hands out elsewhere: that one advances a millisecond *per query*,
  so it runs at whatever rate the image happens to ask the time.

`get_IsInContact` also answers truthfully now rather than always true, for a game that polls
the pointer instead of listening to it.

### A contact sheet, not a photograph

`WPR_SCREENSHOT=path:frame+every` writes a numbered PNG every `every` frames instead of one.
Driving a game blind through its own menus means guessing where to tap and finding out
several minutes later whether the guess was right; a run takes minutes and a frame takes
milliseconds, so one picture per run was the wrong trade by three orders of magnitude.

### The stall that was not a stall

With taps running, the title screen sat on LOADING for ten thousand frames - byte-identical
PNGs from frame 800 to frame 10,400, which reads as a hard freeze.

Two things were wrong, and only one of them was a bug.

**`CreateEventExW` was unimplemented, and unimplemented means zero.** Zero is NULL, NULL is
failure, and this image checks: it threw its own `lang::Exception` reading
`"lang::Signal: CreateEventExW: {0}"`, caught it three funclets deep, and carried on. That
throw is now gone. `SyncLibrary` implements the events, the critical sections and the SRW
locks - locks as no-ops that *succeed*, because there is one thread here and
`InitializeCriticalSectionEx` answers a BOOL the image tests.

An unsignalled wait yields to queued work before answering, because a wait is the clearest
yield point an image ever offers, and then reports `WAIT_TIMEOUT` rather than success:
claiming an event was signalled tells the image that work it is waiting on has finished, and
it then reads whatever that work was supposed to produce.

**The freeze itself was not a freeze.** The tail of the call order at three billion
instructions is `fread`, `operator new`, `memmove`, `memset`, `fread` - the image is loading,
and the title screen simply does not animate while it does. At roughly 2% of a real device's
speed, a few seconds of loading is minutes of ours, and a six-billion-instruction budget was
never going to reach the end of it. The identical PNGs were evidence of a screen that does
not change, which is not the same as an image that is not running.

Worth remembering as a general point about this probe: **a static frame is not a stalled
image**, and the call-order tail settles it in one line. That is why it is now printed.

### Where it is still stuck, and what that is not

Fixing `CreateEventExW` removed the throw and did not move the title screen. At eight billion
instructions - 18,808 frames - it is still on LOADING, and the frames are identical from 3,000
on. The gesture work above is therefore built and correct but cannot yet be aimed at anything:
the slingshot is behind this screen.

What the run says it is doing, at the point it is doing it:

```
last calls, oldest first:
    strlen / memcmp / strlen / memcmp / _Mtx_lock / _Mtx_unlock / realloc / free ...
most called:
    7,816,967  memcmp        3,159,768  strcmp        1,464,142  strlen
```

A string-comparison loop with a mutex and a growing buffer. Five explanations have been ruled
out by measurement rather than by reading code, which is the useful part of this list:

- **Not waiting on an event.** One event is ever created, and it is never waited on: `1
  event(s), 0 signalled, 0 wait(s) satisfied, 0 timed out`. `Concurrency::event` is not it
  either - `wait` is never called on one, though it is now implemented properly (state per
  object, and an unset wait drains the queue before answering rather than claiming the event
  was already signalled).
- **Not starved of queued work.** The deferred queue holds two items in the entire run and
  both ran, early. Each queued callback now reports how much it did, which is the useful
  form: `IWorkItemHandler::Invoke returned 0x0 after 10 import call(s)`. Ten calls is not a
  loader running - the ten are `GetActivationFactory`, two scoped locks, `event::set`,
  `_NewCollection`, `_Schedule` and a delete. It is task plumbing signalling completion, and
  the chore it scheduled did eleven calls of the same. Whatever loads this game's assets, it
  is not these.
- **Not a task collection reporting a lie.** `_RunAndWait` does claim completion without
  running anything, which is the same class of bug - but the image never calls it.
- **Not still reading from disk.** 774 files opened, and the count does not move after the
  first billion instructions. It was reading at three billion; it is not now.
- **Not an unanswered async completion.** The inference added for that - one argument, and it
  looks like a delegate - never once fires on this image. The Xbox object it was written for
  is handed a pointer whose vtable has nothing executable at the Invoke slot, so it is not a
  completion handler.
- **Not the renderer.** Frames present, draws record, the picture is correct. It is drawing
  the same correct thing forever.

So it is a compute loop in the image's own code with nothing outstanding behind it. Finding it
needs a tool this probe does not have: periodic PC sampling to name the hot address, then the
disassembly around it. The runaway detector cannot see it, because that fires on blocks
executed *without a call across the boundary* and this loop calls `memcmp` every few
instructions.


## Where the time went

`PcSampler` answers "where is this image actually executing", which nothing here could do
before. The runaway detector fires on blocks executed *without* a call across the boundary,
so a loop that calls `memcmp` every few instructions is invisible to it - and that was exactly
the loop that needed naming.

Two sources, because they answer different questions at very different prices.

**Call sites are free and always on.** Every trap already knows its caller: it is in `lr`. So
`memcmp was called 7,816,967 times` - which is not a place in the program - becomes:

```
3,508,683 call(s) across the boundary from 1,069 distinct site(s)
   27.1%       951,643  0x00408E9E in 0x00408E55+0x49 -> memcmp
   12.0%       419,760  0x00532ABC in 0x00532A85+0x37 -> strcmp
by function (571 distinct):
   27.1%       951,643  0x00408E55 across 1 call site(s)
```

The `by function` roll-up matters more than it looks: a loop is several call sites, and per
address it spreads across a dozen rows and hides under something merely frequent.

**Blocks are opt-in through `WPR_SAMPLE=n`**, sampling one basic block in n. This needs a
block hook and costs about 35% (123s against 91s on a two-billion-instruction run), but it is
the only thing that sees code calling nothing at all:

```
221,008 block sample(s) at 1-in-1,000 from 4,409 distinct block(s)
by function (738 distinct):
   31.3%        69,263  0x004BF4BD across 153 block(s)
   14.4%        31,920  0x004C13AD across 30 block(s)
```

Neither is a sampling profiler in the usual sense - there is no timer here, and no second
thread that could safely read the CPU - so these count events rather than elapsed time. For
finding a loop that is running when it should not be, a count is the better measure anyway:
exact, and identical on every run.

`WPR_ARGS=0xADDR` then shows r0-r3 for calls from one site, rendering any register that points
at printable text as that text. It keeps the **last** sixteen, not the first: the first are
always startup, and startup is the part that worked.

### What it found, which was that there is nothing to find

The theory it was built to test - that the LOADING screen was a pathological loop - is wrong,
and it took two commands to establish.

The hottest site, 27% of every call across the boundary, is a `std::map` lookup:

```
memcmp(r0="FEATHER_VIOLET_2", r1="MAIN_LOGO", r2=0x9, r3=0x1F)
memcmp(r0="LS_BUTTON_BG_BEACHBALL", r1="MAIN_LOGO", ...)
memcmp(r0="MAIN_BG_BUSHES", r1="MAIN_LOGO", ...)
memcmp(r0="MAIN_LOGO", r1="MAIN_LOGO", ...)
```

The key is constant and the candidates converge on it alphabetically over fourteen
comparisons, ending in a match - a red-black tree descent that **succeeds**. Not a degenerate
hash, not a scan that never finds anything: an ordinary sprite lookup by name. (`r3` is not an
argument at all - `memcmp` takes three. It is a live value in the caller, and it reads 0x0F or
0x1F strictly by string length, which is MSVC's `std::string` small-buffer capacity. Worth
noticing before building a theory on it.)

The second, 12%, is shader parameter binding: `WORLDTM`, `VIEWTM`, `PROJTM`, `TOTALTM`,
`BASEMAP`, `SAMPLER`, `AlphaBlending`, `DepthOn`. Also ordinary, also per-frame.

And a third of all *execution* sits in one function with 153 distinct basic blocks, which is
the shape of a bytecode interpreter's dispatch switch - this title is Lua-driven, and the
neighbouring hot functions cluster with it.

So the image is not stuck in a loop it should not be in. It is rendering a static screen and
running its scripts, competently, at 2% of the speed it was written for. That is a different
problem from the one being looked for, and it is the more useful answer: **there is no bug
here to find**, and the next move is somewhere else entirely.

### What else the counters rule out

Two more things fall out of comparing runs at different budgets, and both are worth having
written down before anyone spends an afternoon on them:

| | 900M instructions | 8e9 instructions |
| --- | --- | --- |
| frames presented | 738 | 18,806 |
| draws | 738 (1/frame) | 71,780 (3.8/frame) |
| files opened | 440 | 774 |

**It was still loading at 900M and had finished by 4e9.** The file count climbs to 774 and
then stops, and the draws per frame go from one to the four of the title screen. So the image
does make progress, and then stops making it - with everything read.

**It is not waiting on a clock.** `QueryPerformanceCounter` is called 10,673 times by four
billion instructions, and this probe's clock advances a millisecond per query at a declared
frequency of 1 MHz, so around sixty seconds of game time passes on that screen by eight
billion. A loading screen with a minimum display time would have long since given up waiting.

The scripts themselves are no help, and it is worth knowing why before trying: everything
under `assets/data/scripts/` is **encrypted**, not plaintext Lua and not Lua bytecode. The
first bytes of `gamelogic.lua` are `8e 27 da da`, where compiled Lua would be `1b 4c 75 61`.
Reading the loading logic statically would mean recovering the decryption first, or dumping
the plaintext out of emulated memory after the image has decrypted it - which is a tool that
does not exist here yet.

### The clock, and the spin-wait hiding behind it

The image computes 255,721 `sinf` and the same number of `cosf` - seventy-seven a frame - and
draws a byte-identical picture every time. Something is being animated that never moves, and
that points at time.

The clock here advanced **once per query**, so time passed at whatever rate the image happened
to read it. That is not just imprecise, it makes the *shape* of a frame impossible: an image
reading the clock three times a frame - top, after update, after draw - gets the same one-unit
gap between all three, so "how long did my update take" and "how long since the last frame"
come back equal. No real clock does that.

Tying it to the dispatcher count at a notional 60Hz fixed the shape and broke something else,
which is the interesting part. **This image has a spin-wait frame limiter**: it reads the clock
in a tight loop until enough time has passed. Against a clock that only moves when the frame
counter does, that loop can never finish inside the frame it is in. The measurements:

| clock | clock reads per frame | instructions per frame | frames at 4e9 |
| --- | --- | --- | --- |
| per query (original) | 3.2 | 1.2M | 3,306 |
| per frame only | **504** | **14.5M** | 276 |
| both (now) | 3.4 | 1.4M | 2,915 |

The original was accidentally immune: a millisecond per read let the limiter out after about
sixteen of them. So time now advances for both reasons - with frames, which gives the right
delta between them, and by 200us per repeated query *within* a frame, capped below one frame
so it can never overtake the frame clock and run time backwards at the boundary.

This is a real fix and it did **not** move the loading screen: 17,609 frames at eight billion
instructions, and every captured frame still byte-identical to `title-screen.png`. Worth
recording anyway - a game whose frame delta is wrong is a game whose physics, animation and
timers are all wrong, and that would have been charged to something else later.

### A cheaper long run

`_callOrder` kept every import ever called: 7.8 million strings on an eight billion
instruction run, hundreds of megabytes, so that forty of them could be printed at each end.
It now keeps the first 64 and the last 64 and counts the rest. The start says how the image
came up and the end says what it was doing when it stopped; the middle had never once been
read.


## Reading the game's own scripts

`WPR_DUMPLUA=dir[:frame]` walks the used heap and writes out anything that looks like a Lua
chunk. It exists because this title ships its scripts **encrypted** - `gamelogic.lua` begins
`8e 27 da da`, where source would be ASCII and compiled Lua would be `1b 4c 75 61` - so the
logic that decides when the loading screen ends cannot be read off disk at all.

It can be read out of memory, because the image has to decrypt it to run it, and that needs to
know nothing about the cipher. Recovering the cipher would be a much larger job for a strictly
worse result.

```
scanning 117 MB of heap from 0x60000000
bytecode-60428600.luac  Lua 5.1 chunk
bytecode-6043ABB0.luac  Lua 5.1 chunk
... 6 script(s) written
```

Two details that matter. **The signature alone is not a chunk** - four bytes of `1b 4c 75 61`
turn up in ordinary data often enough that the first version of this wrote three false
positives out of nine; the version byte must be 0x51-0x53 and the format byte after it zero.
And **timing is everything**: a buffer that was decrypted, compiled and freed only survives
until the allocator hands its memory to something else, which is why the frame argument exists.

### What the scripts said

`strings` over the recovered chunks is enough - Lua stores its constants as length-prefixed
strings - and the answer was immediate:

```
hideLoadingInitXBOX          hideLoadingAchievements       hideLoadingLeaderboards
loadingScreenCallbacks       restUntilCallback             XBOXQueue
disableXBOX                  enableXBOX done               cancelProfileLoading
Loading time - boot to splash                Loading time - splash to menu
scripts/menu/LoadingScene.lua                scripts/menu/LoadingPage.lua
```

**The loading screen waits on Xbox Live.** It rests until callbacks arrive
(`restUntilCallback`, `loadingScreenCallbacks`) and hides itself in three parts - Xbox init,
achievements, leaderboards. This runtime improvises `Microsoft.Xbox.User`,
`Microsoft.Xbox.XboxLIVEService` and `Microsoft.Xbox.Leaderboards.LeaderboardService` as
stand-ins that answer S_OK to everything, so it promises those callbacks are coming and never
sends one. Nothing else in the run says this; the counters, the sampler and the trace all show
an image running normally.

### Making Xbox fail does not help, and why that was worth finding out

The scripts carry a `disableXBOX` path, so the obvious move is to stop pretending. `WPR_XBOX=fail`
does that - every `Microsoft.Xbox.*` method answers E_FAIL - and the image takes it seriously:
it builds the string *"Sorry, we can't sign you in. Please sign in at Xbox.com, then start this
app again."* and then stops on **the same title screen**, byte-identical PNGs for ten thousand
frames.

The first attempt at it was worse and is the more instructive failure. Answering E_FAIL and
**blanking the out-parameter** - which is the correct WinRT answer, and this file's own rule
elsewhere - killed the run after one frame with a null call. The image does
`hr = call(&out); __abi_ThrowIfFailed(hr);`, and that raise is a vccorlib import nothing here
can deliver: the stub returns, the caller carries on believing it succeeded, and dereferences
the null one instruction later.

Delivering that throw properly would not rescue it either. The image carries **no RTTI for
`Platform::Exception`** - `.?AVException@lang@@` and `.?AVCloudServiceException@rcs@@` are
there, `Platform` is not - so it cannot catch one by type even if one arrived. So failure now
reports E_FAIL *and* leaves a live object, which is the one place this probe knowingly breaks
its own "write the out-parameter or report failure, never neither" rule, and it is opt-in
because it changes what the image believes about itself for no gain.

What would actually finish this is the real shape of the Xbox API, and that is what the next
section is for.


## What a stand-in was asked to do

A stand-in that answers S_OK to everything keeps an image running and says almost nothing
about what it should have done. `VtableProfile` turns those calls into the thing needed to
actually implement the class - a slot number, a call count and an argument shape - and
`WPR_SLOTS=<filter>` prints it:

```
Microsoft.Xbox.XboxLIVEService  (2 slot(s) called)
    slot  6 (member  0)  x2    (out*, null, ...)
    slot 11 (member  5)  x1    (out*, null, ...)
<- Microsoft.Xbox.XboxLIVEService::slot11  (1 slot(s) called)
    slot  8 (member  2)  x1    (delegate, out*, ...) <- takes a delegate
<- Microsoft.Xbox.XboxLIVEService::slot6  (2 slot(s) called)
    slot  6 (member  0)  x2    (delegate, 0xFFFFFFFE, ...) <- takes a delegate
    slot  8 (member  2)  x2    (out*, null, ...)
```

**The slot number is the point.** A WinRT vtable is `IInspectable` at 0-5 and the interface's
own members from 6 in metadata declaration order, so `slot 11` is the sixth member and can be
read straight off the class's metadata. That is how a number becomes a name.

Two things this made obvious that nothing else had:

**The factories are not where the surface is.** `Microsoft.Xbox.User`,
`XboxLIVEService` and `LeaderboardService` each get exactly one call - slot 6, which on an
activation factory is `ActivateInstance`. Everything a class can *do* is called on the object
it hands back, and those are placeholders, which were on a different code path and were not
being recorded at all.

**Two of those calls take a delegate.** `(delegate, out*)` is a handler plus a registration
token; `(delegate, 0xFFFFFFFE)` is a handler plus what is very likely a sentinel. Both were
accepted and neither was ever invoked - which is exactly what `restUntilCallback` and
`loadingScreenCallbacks` in the recovered scripts are waiting on.

### Calling them back

The placeholder path now completes a delegate handed to it, writing the token first if there
is one. The callbacks fire:

```
async  Microsoft.Xbox.XboxLIVEService::slot11/slot8(handler 0x6010A2F0) -> completed at once
async  Microsoft.Xbox.XboxLIVEService::slot6/slot6(handler 0x60CD4740) -> completed at once
async  Microsoft.Xbox.XboxLIVEService::slot6/slot6(handler 0x60CD4B20) -> completed at once
```

and the image responds: `ActivateInstance` goes from one call to two, the service instance is
asked for `slot 8 (member 2)` twice where before it was never asked at all, and the whole
run's boundary traffic nearly triples. The handshake is advancing.

It still does not reach the menu, and turning the slot numbers into names is what the next
section does - with no guessing required, because the metadata is in the XAP.

The same inference had been added to the *discovery default* earlier in the session and never
fired once. It was on the wrong path, and only the slot dump showed which path was the right
one - which is a fair summary of why the tool was worth building.


## The metadata is in the package

`Microsoft.Xbox.winmd` ships **inside the XAP**, next to `Microsoft.Xbox.dll` and `xbl.dll`.
A WinRT component has to carry its metadata for anything to bind to it, so the authoritative
answer to "what is slot 8" was in the game the whole time. No SDK, no guessing.

Reading it takes about eighty lines against `System.Reflection.Metadata`, which is in-box in
.NET 8: open the file with `PEReader`, walk `TypeDefinitions`, and print each type's methods in
order. **Declaration order is vtable order**, so numbering from 6 lines the output up with the
slot dump directly.

Two things to know before trusting a number. **A "member N" here is a vtable ordinal - slot
minus six - not a metadata table row.** TypeDef `0x02000002`, MethodDef `0x06000002` and
MemberRef `0x0A000002` are global row indices into the whole file and have nothing to do with
an interface's third member; two different interfaces both have a member 2 and they are
unrelated. And the answer genuinely differs per interface, which is why the chain has to be
followed object by object rather than resolved once.

### The chain, resolved

| what the slot dump saw | what the metadata says it is |
| --- | --- |
| `XboxLIVEService` slot 6 `(out*)` | `SignInAsync()` -> `IAsyncOperation<UserIdentity>` |
| `XboxLIVEService` slot 11 `(out*)` | `get_ServiceClient()` |
| `<- slot6` slot 6 `(delegate)` | `IAsyncOperation::put_Completed(handler)` |
| `<- slot6` slot 8 `(out*)` | `IAsyncOperation::GetResults(UserIdentity**)` |
| `<- slot11` slot 8 `(delegate, out*)` | `IServiceClient::add_SignedOut(handler)` -> token |

Every argument shape the profiler inferred is confirmed by the signature, which is a good sign
for the profiler: `(out*, null)` really was a no-argument call returning through a pointer, and
both "takes a delegate" flags were right about the delegate and wrong about nothing.

### And it caught a bug I had just introduced

Completing *any* delegate handed to a placeholder is wrong, and the metadata says exactly how
wrong. `IServiceClient::add_SignedOut` is an **event registration**, so firing it on
registration tells the game the player just signed out - at the moment it is trying to sign
them in.

The distinguishing signal is the one the discovery default already used and the placeholder
path had not: a trailing stack out-parameter is a registration token, and a call that wants one
is `add_X`, not `put_Completed`. With that in, one completion fires where three did, and it is
the right one.

### Implemented, from the metadata

`XboxRuntime.cs` implements the five interfaces the image binds, plus the four it reaches
through them - `IUserStatus`, `IUserProfile`, `IAsyncOperation`/`IAsyncAction` and `IAsyncInfo`
- with every slot annotated with the signature it stands for so the code and the winmd can be
checked against each other. It reports a signed-in player with an empty everything-else: no
friends, no achievements, no leaderboards, no messages. That is a lie, but it is the *shape* of
the truth, and it is a shape the image is written to cope with where "the call never came back"
is not.

The handshake now runs end to end, which the trace shows plainly:

```
IXboxLIVEService::SignInAsync
IAsyncOperation<SignInAsync>::put_Completed
IAsyncOperation<SignInAsync>::GetResults
IUserIdentity::QueryInterface / AddRef x6
IXboxLIVEService::get_ServiceClient
IServiceClient::add_SignedOut
IUser::get_Identity
ILeaderboardService::GetLeaderboardsAsync
```

`Microsoft.Xbox` has disappeared from the "reached, but not implemented" list entirely.

Two details worth keeping. **An out-parameter is not always r1**: `GetAchievementsAsync` takes
four arguments so its out is the fifth, `GetLeaderboardAsync` takes six, and `PostResultAsync`
carries an `Int64` that AAPCS pushes to the stack rather than splitting across r3, which moves
the out-parameter again. Each of those is annotated where it is implemented. And **QueryInterface
now answers one IID for real**: `IAsyncInfo` and `IAsyncOperation` are different interfaces on
the same object whose members collide - `get_Status` is IAsyncInfo member 1, which is
`get_Completed` on IAsyncOperation - so answering every IID with the same object would report
an operation's status as whatever `get_Completed` returned, which is zero, which is
`AsyncStatus::Started`. A caller reading that waits for ever on something that finished before
it asked. This image never asks, as it turns out, but the collision is real and the general
QueryInterface lie is now overridable per slot.

### It did not reach the menu at this point

Every Xbox call was answered with the right shape and type and the title screen was unchanged at
ten thousand frames. The leaderboard operation was referenced four times and released without
ever completing, and `GetAchievementsAsync` was never called. What was actually wrong is in
*Past the loading screen* below; the short version is that Xbox was necessary and not
sufficient, and the two remaining faults were both this runtime's.


## Reading the bytecode

`strings` over a Lua chunk gives the constants and nothing else - no control flow, no calls,
no idea which function any of it belongs to. Lua 5.1's undump format is small enough to parse
outright, so the scratch tool alongside this probe disassembles the recovered chunks. The
result is the game's own logic, legible:

```
=== LoadingScene:11-14  params=1
    0  GETTABLE  r1 = r0["pages"]
    1  GETTABLE  r1 = r1["loadingPage"]
    2  SETTABLE  r1["visible"] = True
    ...
=== LoadingScene:16-19  params=1
    2  SETTABLE  r1["visible"] = False
```

**`lua_Number` is a float in this build, not a double.** The header declares it - byte 10 is 4,
not 8 - and assuming otherwise desynchronises the constant table and fails the parse hundreds
of bytes later, somewhere that looks nothing like the cause. Four of six chunks failed that way
before the size was read from the header rather than assumed.

Two fixes to `ScriptDumper` came out of using it. It can now scan **repeatedly**
(`WPR_DUMPLUA=dir:frame+every`), because one snapshot catches almost nothing - a script is
decrypted, compiled and freed, and the buffer lives only until the allocator reuses it. And the
content hash is now part of the **filename** as well as the dedupe: names built from the address
alone silently overwrote each other, which is how 58 chunks written became seven files on disk.

### What the scripts do and do not contain

124 chunks captured across a dense scan - and only **five distinct scripts**: `LoadingScene`,
`MainMenuPage`, `SceneGraph`, an animation helper, and `RovioAccount`. Everything else is
compiled and freed before any scan can see it. Scanning cannot fix that; catching the rest means
hooking the loader itself, so the chunk is copied at the moment it is handed to Lua rather than
hunted for afterwards.

`LoadingScene` turns out to be a thin wrapper - `onEntry` shows the page, `onExit` hides it - so
the decision to leave it is in the scene state machine, which is one of the scripts not
captured.

### A second subsystem, and one more thing ruled out

`RovioAccount` is not Xbox. It binds a `native_RovioAccount` global and carries
`isAccountLoggedIn`, `login`, `loginRegisteredAccount`, `showLoadScreen`, and the image's own
`rcs::CloudServiceRovioLoginRequiredException`. A second thing the loading screen could
plausibly be waiting for.

It is not waiting for it. **`WS2_32.dll` is imported - ten functions - and never called once.**
No socket is opened, no name is resolved, nothing is sent. Whatever the Rovio cloud layer is
doing, it is not reaching the network, so the offline path is not being blocked on a connection
that never completes.

Which is worth writing down mostly for what it costs later: those ten imports return zero like
every other unimplemented one, and zero is a *valid* socket descriptor where -1 is the failure
value. The first title that does reach for the network will find `socket()` succeeded.


## Past the loading screen

`main-menu.png` is frame 1,200: the Angry Birds Rio main menu - LEADERBOARDS, ACHIEVEMENTS,
settings - under the game's own error dialog for the Xbox call it has just seen fail, with a
tick to dismiss it. The clear colour in the report changes from white to sky blue at the same
moment, and the draw count per frame from four to ten. Four things had to change, and the tool
that found each one is the point of recording them.

### Every script, caught on its way out

The heap scan found five scripts because the rest were compiled and freed before any scan could
run. So the hook is not the loader - it is **`free`**. A decrypted chunk is handed to
`lua_load` and released through this probe's own free stub, because the CRT is ours, and at
that moment it is complete, contiguous, still there, and its exact size is known to the
allocator. `ScriptDumper.Capture` peeks six bytes at every free while a dump is wanted: 231
chunks, all of them parsing, **109 distinct scripts**.

Reading them settled the question the previous fortnight could not. `XBOX.lua` defines
`hideLoadingInitGameCenter`, and **no script calls it** - the exe carries the string, so it is
the C++ side that does, with `lua_getglobal`, when *its* Xbox init completes. The Lua function
itself only clears a flag. So the loading screen was waiting for native code to finish a job,
and the trace of what that native code did with our sign-in result is the next section.

### A continuation nobody ran

`StartCallCapture` records every call made between a completion handler being invoked and
returning - the image's verdict on what it was handed, which an S_OK return can never say. The
sign-in handler's verdict was nine calls: `GetResults`, reference the identity,
`CoGetObjectContext`, **`Concurrency::event::set`**, done. That is a PPL `.then()` being
scheduled, and the deferred-work log then ended on

```
queued _UnrealizedChore::_Invoke at 0x0049B8A1 (1 pending)
```

with no `->` line after it. The continuation was queued and never ran: on a device it would run
on a pool thread while the UI loop turns, and here a queued chore runs only at a yield point -
`Concurrency::wait`, an event wait, a lifecycle boundary - and the main loop is
`ProcessEvents -> Present -> ProcessEvents` and touches none of them. The earlier verdict that
"the queue holds two items and both ran" was true when it was written; the sign-in callback fix
added a third, and nothing drained it.

`ICoreDispatcher::ProcessEvents` now drains the queue before pumping, which is what "process
events" means on a real dispatcher anyway. It drains only when something is queued, so the
continuation always returns through a trap rather than inline in the stub.

### The factory is not the instance

Running the continuation killed the run at once, with a null call whose registers held the
"Sorry, we can't sign you in" message and the error code -2. The vtable dump then showed
`IUserIdentity::slot11 (r1=0, r2=0x64)` - slot 11 with `(0, 100)` is
`IUser::GetAchievementsAsync(0, 100, ...)`, being called on what the image believed was its
`User`. It was a `UserIdentity`, because `Microsoft.Xbox.User`'s **factory** slot 6 had been
mapped to `IUser::get_Identity` when on a factory slot 6 is `IUserFactory::CreateUser(xuid,
gamertag)`. The image took what it got back for a User, called slot 11 of an eight-slot
object, the out-parameter nobody wrote became a null task, and the `.then()` on a null task is
the -2. `XboxRuntime` now has factory objects for `User`, `UserIdentity`, `ServiceClient` and
`LeaderboardService`, each telling `ActivateInstance` from `CreateXxx` by whether r1 is an
out-parameter. `XboxLIVEService` stays as the interface itself, because the trace says so.

### Three faults in the runtime that the game then exposed

- **`WinRtRuntime.Arg` stopped at r3.** `GetAchievementsAsync` has four inputs, so its
  operation returns through the fifth argument, on the stack. The stub threw. Host-stub
  failures now print their stack frames - "Parameter 'index' out of range" comes from every
  list indexer and from `CallFrame.Arg` alike, and the message alone cannot tell them apart.
- **A constructor stub treated "non-zero" as "already has a vtable".** `ConstructShapedObject`
  leaves an existing vptr alone so a derived constructor's work survives its base - but an
  object carved from a recycled block is non-zero at offset 0 whatever it used to hold. A
  `Platform::Exception` was built in a block whose first word was its own address, a dead list
  sentinel; the throw read that as a vptr, read it again as the method, and the CPU branched
  into the object. The guard now asks whether the word points at code. `Platform::Exception`
  constructors also get a real vtable and keep their HRESULT at `this+4`, and the report lists
  every exception the image constructed with the HRESULT in it - a shorter list of what is
  failing than anything else this probe prints.
- **A vtable exactly as long as its interface.** With QueryInterface answering every IID with
  the same object, the image calls `IIterable` members on an `IVectorView` and vice versa, and
  a slot one past the end is the next heap block. Every discovery vtable is now padded to 32
  trap slots, so the same mistake prints as `IVectorView::slot9` instead of ending the run.

### What the game does with an empty world

Signed in, no friends, no achievements, no leaderboards, the image constructs
`Platform::Exception(0x82BC0008)` in its leaderboards continuation and shows the generic
"Sorry, we're not sure what happened" dialog over the menu. That is correct behaviour on both
sides: the leaderboard metadata *is* empty, and the game says so and carries on. Populating the
collection from `leaderboards.lua` - now readable - would remove the dialog; it has not been
done, because a dismissable dialog on a working menu is a much better place to be than a
loading screen, and the next thing to know is whether a level runs.


## Taps

Mouse clicks on the live host did nothing, and neither, it turned out, had any scripted tap
ever done anything: the splash screens advance on a timer, and every apparent success had been
that. Three faults stacked, and the order they were found in is the useful part.

**The pointer position was never read - or so it looked.** Capturing every call the
`PointerPressed` handler makes (the same `StartCallCapture` that had shown what the sign-in
continuation did) gave, for 1,480 pointer events across a grid of taps: `get_PointerId` twice
and `get_Timestamp` once per press, and `get_Position` **never**. A game that does not read the
position cannot hit a button, so at that point the mystery was how it ever worked on a device.

**It was reading the position. Our vtable was wrong.** `Windows.UI.Input.IPointerPoint`, in
the order `Windows.UI.winmd` declares it, is PointerDevice, Position, **RawPosition**,
**PointerId**, FrameId, **Timestamp**, IsInContact, Properties. This runtime had PointerId at
slot 8 and Timestamp at slot 9. So the two calls logged as `get_PointerId` were
`get_RawPosition` - the game reading its touch position twice per press, and being handed a
32-bit `1` where it expected an eight-byte `Point` - and the call logged as `get_Timestamp` was
`get_PointerId`, handed a 64-bit timestamp. Every tap ever delivered landed at
(1.4e-45, whatever came next on the stack). The metadata that settles this is on every Windows
machine at `C:\Windows\System32\WinMetadata\Windows.UI.winmd`, readable with the same
eighty lines that read the Xbox winmd, and the lesson generalises: **a WinRT slot number
guessed from documentation order is a guess; the winmd is the fact.** `ICoreWindow` and
`IPointerEventArgs` were checked the same way while the file was open.

**The orientation was a placeholder too.** The image calls `put_AutoRotationPreferences(5)` -
Landscape|LandscapeFlipped - and rotates every pointer position itself according to
`DisplayProperties::CurrentOrientation`. That was unimplemented, so the discovery default wrote
an object pointer into it and the game read its orientation as a number in the billions. It now
answers `Landscape` (1), and `NativeOrientation` answers `Portrait` (2), because WVGA is a
portrait device.

With those two fixed, the rotation applied on the way out of `get_Position`/`get_RawPosition`
could finally be tested. Positions are held in the landscape space the composition is drawn in,
and the image expects them in its portrait window space. `WPR_ROTATE=ccw` (the default - the
phone turned so its buttons are on the right, WP8's "Landscape") maps landscape (x, y) to
window (480 - y, x); `cw` is the other quarter turn; `none` passes them through. One tap on the
Xbox dialog's tick, under each: **ccw dismissed it**, the other two did nothing. `main-menu.png`
is the frame after - the PLAY button, with the flock crossing the sky behind it, because the
menu animates once it is allowed to.

Two smaller things came out of the same work. `WPR_TAPHOLD` sets how many `PointerMoved`
events a scripted tap carries between press and release (default 8, the shape of a real
finger; 0 for a bare press-release), added to rule out a menu engine that treats any move as a
drag - it was not that. And the desktop host's title bar now shows the count of taps delivered
and where the last one was handed to the image, because "the mouse does nothing" and "the mouse
was delivered to the wrong place" are indistinguishable on screen and that line tells them apart.

### What input still is not

One pointer. No pinch, no second finger, and the emulator delivers one event per turn round
the image's main loop, so a drag is as many frames long as it has steps. The mouse-as-touch
path is verified to the main menu and no further; whether a level's slingshot takes a drag from
it is the next thing to find out, and the machinery to find out is all here now.


## Three screens further in

With a script that can wait as well as touch, the game drives from cold start to a level
listing without a human:

```
WPR_TAP=60 WPR_INPUT="wait:1150;tap:624,360;wait:120;tap:400,288;wait:120;tap:290,200;wait:120"
```

`menu-clean.png`, `episode-select.png` and `level-select.png` are frames 1,300, 1,500 and 1,700
of that run - the main menu once its error dialog is dismissed, the episode list (SHOP,
PLAYGROUND, SMUGGLERS' DEN, JUNGLE ESCAPE, BEACH VOLLEY, and the Rio / Rio 2 tabs), and the
level page behind one of them. Every transition is a tap this probe delivered, at a coordinate
chosen from the previous screenshot.

### The drag works, and here is the proof

A drag had never been shown to do anything - the earlier gestures all ended on screens that a
tap alone could dismiss. The level page is a scroller, so it is the first screen that can
answer the question. Dragging across it changes **two frames out of a run of a hundred**, and a
pixel diff puts the whole difference inside a 39x55 box at x 754..792, y 213..267: the page
chevron, reacting while a finger is down and reverting on release.

A horizontal drag does more than react - it turns the page. Five distinct frames of animation,
then a new resting frame with the chevron moved to the *left* edge, and a drag the other way
returns to a frame **byte-identical** to the one before. That is a scroller with two pages,
driven entirely from injected pointer events.

### What the level page gets wrong

Each page holds **one column of three tiles**, and there are two of them - six. The pack on
disk has fifteen: `assets/data/levels/` holds 39 packs, the regular ones (`airport1`,
`airport2`, `jungle1`, ...) fifteen levels each and the `bonus*` ones four. So the page is
missing nine tiles out of fifteen, and the column it does draw sits about 150px too high - the
first tile is cut off by the top edge.

The screenshot report says the same thing from the other side: frame 1,700 is **25 draws, 94
triangles**, with the three tiles at x 72..163 and y -46, 53, 153. They are not drawn wrongly.
They are the only three that exist.

### The wall: the game stops itself

Tapping the one unlocked tile ends the run:

```
stopped   the image threw .?AVLuaException@lua@@; unwound 3 frames, no matching catch found
message: "bad argument #1 to 'pairs' (table expected, got nil)"
message: " (call stack not available)"
```

The dumped scripts name the site exactly. `PageGrid` has four methods, and one of them is

```lua
function PageGrid:getPage(gridX, gridY)
    for _, page in pairs(self.pages) do ...
```

while `self.pages` is created in `PageGrid:init` and nowhere else. So a page grid whose `init`
never ran was asked for a page - which is also the most likely reason the level listing is
three tiles instead of fifteen. One missing initialisation, two symptoms.

**This is the image's own code failing, not the emulator refusing to run it.** The distinction
matters for what to do next: there is no unimplemented import here, no stubbed vtable slot, no
fault domain - the CPU ran every instruction the game asked for, and the game's Lua decided it
had nothing to iterate. Finding out *why* that init is skipped means following the menu engine's
scene construction, which is Lua, which is now readable.

Worth noting what the run still does after that: it presents 1,810 frames, opens 774 files with
two expected misses (`devconfig.json` and `highscores.lua`, neither of which exists on a first
run), and keeps its trap page intact to the end.


## A format, not a missing sprite

The episode list drew a 38-pixel column of horizontal stripes between the last panel and the
edge of the screen. It looked like a sprite that had failed to load, or a UV that had run off
the end of an atlas.

It was neither. The per-draw dump names the texture *and its format*:

```
draw: 6 indices, tex Texture2D46 523x506 fmt 115
```

**DXGI format 115 is `B4G4R4A4_UNORM` - two bytes a pixel**, which is how a phone game stores
its UI to halve the memory it costs. `Direct3DRuntime` sized every uncompressed texture at four:

```csharp
resource.RowPitch = resource.PixelWidth * 4;   // wrong for 85, 86 and 115
```

So each uploaded row went into a slot twice its real width, half of every row was dropped, the
rest landed on the wrong scanline, and `Sample` then read four bytes per texel out of two-byte
pixels. Stripes.

What was actually missing is bigger than the artefact: the whole right-hand foreground of that
screen - a tree trunk, its leaves and a purple flower - had never been drawn at all.

`Resource.PixelBytes` now carries the size, and `Sample` decodes all three 16-bit layouts.
One detail worth keeping: the channels are **expanded, not shifted**. Four bits of `0xF` must
come out 255 rather than 240, or every white in the interface is quietly grey - which reads as
a stylistic choice, not a bug, and would have survived any number of screenshots.

**This will have fixed more than one screen.** A WP title draws most of its UI from 4444
atlases, so anything that looked flat, striped or absent is worth another look.


## Why it is slow, measured

"It takes a minute to reach the menu" had three plausible causes and the profile killed two
of them.

The run prints a timeline now, taken from the colour the image clears to - a game announces
its phases that way, and seconds are the currency the question is asked in:

```
-- HOW THE LOAD SPENT ITS TIME
     10.7s  frame      0  clears to (1.00,1.00,1.00)
    139.1s  frame    302  clears to (0.30,0.76,0.90)
```

Ten seconds before the first frame - 195 C++ static initialisers and the Lua bootstrap - then
**302 frames of loading in 128 seconds, 424ms each**. Frames after the menu are far cheaper.
So the load is not waiting on anything: it is compute.

### The two things it is not

**Not the host stubs.** Every stub is timed now, and the whole set accounts for **2%** of a
run. The most expensive single entry is `memcpy` at half a microsecond a call.

**Not the boundary.** A crossing into a stub costs **0.16us**, and the binding underneath it
is 25ns per register read. At 3.7 million crossings that is half a second in a three minute
run. `memcmp` and `strcmp` between them are 43% of all crossings - the obvious thing to
optimise - and rewriting them would save almost nothing.

### What it is

Guest **stores**. The benchmark that matters is not the headline one:

| loop | Unicorn, no hook | Unicorn, code hook |
| --- | --- | --- |
| `subs r0,#1 ; bne` - the headline figure | 788 MIPS | 169 MIPS |
| `ldr` alone | 792 MIPS | - |
| `str` alone | **29 MIPS** | 28 MIPS |
| a realistic mix - load, add, store, call, branch | **57 MIPS** | 46 MIPS |

A store costs about **96ns**, twenty-five times a load and seventy times an ALU operation,
and it is the same with the page mapped non-executable, so it is not write-protection
tracking - it is Unicorn's write path, which cannot use the TLB fast path the read path uses.
The image is 11% stores (**210,097,414** of them in two billion instructions, counted through
the write hook that was already there), and in the mixed loop that one store in eight
instructions is 68% of the time.

**The code hooks are worth 1.24x, not 4.7x.** Any `UC_HOOK_CODE` stops Unicorn chaining
translation blocks, and on the two-instruction loop that costs 4.7x - which is why the
headline benchmark shows it. On code shaped like a real program the same hook costs 46 vs 57
MIPS, because real code leaves a basic block every few instructions anyway. Removing the
import trap's code hook is a deep refactor of the trap mechanism for a quarter of a
speed-up. **Measure the mix before paying for the fix.**

### What would fix it

The same two loops under dynarmic, which is already built and licence-clean:

| loop | Unicorn | dynarmic | |
| --- | --- | --- | --- |
| tight ALU | 788 MIPS | 3,384 MIPS | 4.3x |
| realistic mix | 57 MIPS | **1,794 MIPS** | **31x** |

dynarmic's figure is from its *slow* memory path - `UserCallbacks`, a virtual call per access
- because that is what the prototype wires up. A page table would be faster again. Thirty-one
times would turn the 128 second load into about four seconds, which is roughly what the game
takes on the phone it was written for.

That is the case for the port, and it is now a measurement rather than an argument.

### A knob that did not help, kept because the answer is useful

`WPR_CLOCK` sets how far the virtual clock advances per frame (microseconds, or `auto` to
follow the host's own clock). The default, 16,667us, tells the image it runs at exactly 60fps
however long a frame really took - which is what makes a run reproducible, the same tap
landing on the same frame every time.

The theory was that the load is mostly the game waiting on timers, so a faster clock would
skip it. It is not, and it does not: at `WPR_CLOCK=100000` the game reached **47 frames in
three billion instructions** - 64 million instructions per frame against 2.85 million - because
a large delta puts its own catch-up loop to work. Fewer, much more expensive frames. The knob
stays for experiments; the load is compute, and only the CPU can fix it.


## Licensing — this engine cannot ship

**Unicorn is GPLv2. WPR is MIT.** Verified 2026-09-01 against
`unicorn-engine/unicorn`'s own `COPYING` ("GNU GENERAL PUBLIC LICENSE Version 2, June 1991")
and its README ("Distributed under free software license GPLv2"); the repository also carries
`COPYING.LGPL2` and `COPYING_GLIB` for vendored pieces, but the project as distributed is
GPLv2. The `UnicornEngine.Unicorn` NuGet package declares no licence at all and ships no
licence file, which is why this was worth checking rather than assuming.

MIT and GPLv2 are compatible in the direction that matters - MIT code may be combined into a
GPL work - but the *combined distribution* then has to be GPLv2. Shipping `unicorn.dll` or
`libunicorn.so` inside a WPR installer or APK, next to a WPR component that P/Invokes it, is
the case the GPL exists to cover. WPR could not continue to describe that build as MIT.

**Nothing is exposed today.** This probe is in no solution and no CI workflow, `unicorn.dll`
is gitignored, and a `PackageReference` in unbuilt research source is not distribution of a
binary. The exposure begins at the exact moment a shipping project takes the dependency.

Three ways out, and the choice is a project decision rather than a technical one:

1. **Relicense the shipped product as GPLv2.** Lawful and simple, but it is not this
   document's call - the `LICENSE` copyright holder is MediaExplorer.
2. **Never distribute Unicorn.** Support WP8 native only when the user supplies their own
   `unicorn.dll`, which is already exactly what `run.ps1` does. Commonly done, and it avoids
   the distribution trigger; it is not risk-free, because the FSF's position is that a
   program designed to link a GPL library forms a combined work regardless.
3. **Use a different CPU.** `dynarmic` is **0BSD** - confirmed on the three live forks, the
   original `merryhime/dynarmic` having gone - and it is a *recompiler* rather than an
   interpreter, so it addresses the throughput problem in the same move. Its `A32` frontend
   is ARMv6K/ARMv7A, which is precisely WP8's Thumb-2, and the actively maintained forks are
   Vita3K's (PS Vita is a Cortex-A9, the same architecture as our target) and azahar's.

Option 3 is the one that makes a shipped WP8 runtime possible, and it fits better than it
looks. Dynarmic leaves memory to the embedder - a 4 KB-granular `page_table`, or a
`fastmem_pointer` over a host reservation - and this probe already owns its whole address
space. The trap mechanism gets *simpler*: instead of IAT slots pointing at a page of
`bx r12`, each slot becomes an `svc #n` and `CallSVC` hands us the index directly.

What it costs is this harness. Dynarmic has no hook API, so the write watches, the block
statistics, the runaway detector and the heap-execution guard - the tooling that found most
of the bugs recorded above - have no equivalent and would have to be rebuilt or lost. The
sane split is therefore to keep **Unicorn as the research harness, never distributed**, and
target **dynarmic for the product**.

Neither can be built on this machine's Windows side: there is no cmake, no MSVC, no clang,
no gcc and no NDK. WSL has cmake 4.2.3 and g++ 15.2, so a linux-x64 evaluation is possible
today on the fallback path `run.ps1` already uses - and that is enough to answer the only
question that matters first, which is how much faster it actually is.


## Throughput, measured

The self-test's 787 MIPS is a two-instruction loop that translates once and spins. Against a
real image the figure is **~22 MIPS**, and three measurements say where it is not going:

| run | wall | rate |
| --- | --- | --- |
| 300M instructions | 12 s | 25 MIPS |
| 2e9 instructions | 87 s | 23 MIPS |
| 4e9 instructions | 183 s | 22 MIPS |

- **Not the trap path.** Timing the trap handler directly: 566,700 traps cost **449 ms of a
  12 s run**, and 3.85M traps cost 2.4 s of 183 s - under 4%, then under 2%. At 0.79
  microseconds each, host dispatch is doing its job. Worth knowing before optimising it,
  because 812,603 import calls in a 900M-instruction run *looks* like the answer.
- **Not the write watch.** Removing the whole-heap `AddMemWriteHook` and re-measuring gave
  38 s both times, to the second.
- **Not cold-code translation.** The marginal rate between the 2e9 and 4e9 runs is 20.8 MIPS,
  which is the same as the average. If translation dominated, the rate would climb as code
  went hot. It is flat, so this is simply what Unicorn costs on this workload.

**It is partly the trap mechanism, in a way that cannot be optimised.** The self-test now
runs its loop twice, once with a code hook installed over a range the loop never enters:

| | |
| --- | --- |
| no hooks | 774 MIPS |
| one code hook | **168 MIPS** |

The hook never fires. The cost is not the callback - it is that having *any* `UC_HOOK_CODE`
stops Unicorn chaining its translation blocks, so every block exits to the dispatcher. That
is a 4.6x tax on the whole image, and this probe cannot avoid it: the trap page **is** a code
hook, and it is the entire host-call mechanism.

It is not an artefact of the MinGW build this repo uses on Windows. The same self-test under
WSL, on the package's own `libunicorn.so`, reports 962 MIPS and 312 MIPS - a 3.1x tax on a
different build of a different binary on a different platform.

So the ceiling is 168 MIPS rather than 774, and the ~7.6x from there down to 22 is the honest
difference between a real workload and a two-instruction ALU loop.

For scale, a WP8 device is a ~1 GHz ARM, so this is on the order of 2% of the real thing.
`dynarmic` runs the *identical* Thumb-2 loop at **2,491 MIPS**, and - the part that matters -
it needs no code hook for any of this: an IAT slot becomes `svc #n` and `CallSVC` is a
first-class thing the recompiler already handles. Compared like for like, against the 168
MIPS this design actually gets rather than the 774 it would get without traps, that is a
**~15x ceiling**, not 3.2x. The real workload will not move by the whole of that - the 7.6x
residual is complexity both engines pay - but the 4.6x is structural, measured, and avoidable
only by changing engine.

**But the number that decides this has not been measured, and cannot be yet.** The marginal
rate above is ~708,000 instructions per presented frame, i.e. ~29 fps - on a splash screen
with four draws, where the game is idling. Nobody knows what a frame of actual gameplay costs
because gameplay is unreachable: firing a bird needs a drag, and input is press-and-release at
the screen centre. So the honest order is **input first, then measure a real frame, then pick
a CPU** - not the other way round.


## Known gaps

- **No TEB.** Unicorn 2.1.3 treats `UC_ARM_REG_C13_C0_2` (TPIDRURW) as a no-op, so the
  thread environment block is not really installed. Nothing has needed it yet; threads
  and SEH will, and it must then go through `UC_ARM_REG_CP_REG`.
- **No relocations.** The image is mapped at its preferred base, which works because
  nothing else occupies it. A second module would need `.reloc` applied.
- **Placeholder objects answer S_OK to everything.** They keep the image running and are
  how the chain above was found, but every value they hand back is a lie. Each one that
  shows up in the to-do list is a real class waiting to be written.
- **The allocator frees but does not coalesce.** Exact-size buckets over a gigabyte -
  sixteen-byte granularity below a kilobyte, powers of two above it. Blocks come back
  when the same size is asked for again and not otherwise, so a workload that never
  repeats a size still climbs.
- **Only the first throw is handled well.** One catch has been entered successfully; a
  throw with no matching handler, a rethrow, or a nested throw inside a funclet are all
  untested. Nothing tracks an in-flight exception across those cases.
- **File I/O is implemented but barely exercised.** The image opens exactly one file before
  it throws, so `fread`, `fwrite`, `fseek` and the Win32 layer are written but untested
  against real use. That waits on the handler transfer.
- **No real concurrency.** Deferred callbacks are not threads: anything expecting to run
  while its caller continues deadlocks rather than waits. Events, critical sections and SRW
  locks exist now (`SyncLibrary`) but they are single-threaded impersonations - a lock always
  succeeds because it can never be contended, and an unsignalled wait yields once and then
  reports a timeout.
- **No `sscanf` or `strftime`.** The printf side of varargs works; scanning a string back
  into caller-supplied pointers does not. `CrtLibrary.NotImplemented` lists them.
- **D3D11 records, it does not render.** Device, context, DXGI chain, swap chain and the
  resource types exist and every call is logged, and `FrameCapture` rasterises a frame in
  software - but nothing reaches a GPU. Shader bytecode is discarded at `CreateVertexShader`
  rather than kept, which is the first thing a real bridge would need.
- **The rasteriser runs no shaders.** Positions come straight from the vertex buffer through
  one constant-buffer matrix; there is no vertex shader, no pixel shader, no depth buffer, no
  blend state, no filtering and no mip selection. It draws what this title's 2D sprite path
  asks for and would draw a 3D scene wrongly.
- **Yielding is cooperative and only `Concurrency::wait` yields.** Any other blocking
  primitive the image reaches - an event wait, a join, a lock held across a handoff - still
  deadlocks rather than waits.
- **Three Xbox Live classes are stand-ins.** `Microsoft.Xbox.User`,
  `Microsoft.Xbox.Leaderboards.LeaderboardService` and `Microsoft.Xbox.XboxLIVEService`
  answer every call with a placeholder. The game believes it is signed in.
- **Audio is silent by construction.** The engine and voices exist and accept everything;
  no sample is ever mixed, and `GetState` always reports an empty queue.
- **The unwinder is not exact.** Two frames of a twelve-frame walk came back with a state
  the IP map does not contain and with handler data at a negative RVA. The walk survives
  both, but a handler search across those frames cannot be trusted.
- **Input is scripted, and only one pointer.** `WPR_INPUT` does taps and drags at named
  coordinates (see *Input is a script, not a tap*), which is enough for a slingshot. There is
  no multi-touch, no pinch, nothing driven by what is on the screen, and the Closed and
  VisibilityChanged subscriptions are still accepted and never fired.
- **Asynchronous operations complete instantly, by inference.** A discovery-default method
  taking one argument that looks like a WinRT delegate is treated as `put_Completed` and
  answered at once. That is the right shape for the WinRT async pattern and nothing else in
  WinRT looks like it - an event registration wants a token through a second argument - but
  it is a guess made from a call signature, not from knowing the interface. What fired is
  printed with the run, which is how you check.
- **The window size is a constant.** 480x800 - WVGA portrait, which is what a WP8 CoreWindow
  is - with the frame rasterised into the 800x480 landscape the game composes in. Nothing
  negotiates either with a real surface, and `WPR_WINDOW` / `WPR_SCREEN` are the only way to
  ask for anything else. A title that reads the bounds and lays out portrait would need the
  pair swapped by hand.
- **QueryInterface lies.** It returns the same object for any IID asked for, which holds
  only because these statics implement one interface each.
- **Unimplemented imports return 0.** Fine for a void or an ignored handle, a lie
  anywhere else. `HostStubs` covers only the startup path.
- **Single-threaded.** A Unicorn context is one thread; the WinRT thread pool is not.

## Background

Full feasibility study, including why running the ARM code natively is not an option on
either Windows or Android any more:
<https://claude.ai/code/artifact/e49413ae-2e98-4368-84c0-69262cae134f>
