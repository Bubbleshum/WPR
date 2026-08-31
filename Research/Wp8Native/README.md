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

```
dotnet run -c Release -- <path-to-arm-exe> [instruction-budget]
```

Point it at the executable inside an unpacked XAP (a XAP is a plain zip):

```
dotnet run -c Release -- ./AngryBirdsRio.exe
```

### The native library problem

`UnicornEngine.Unicorn` 2.1.3 ships native runtimes for **linux-x64, linux-arm64,
linux-ppc64le and osx-x64 only**. There is no `win-x64` `unicorn.dll` in the package, so
on Windows the static analysis runs and then the CPU fails to start with
`DllNotFoundException`. Two ways round it:

**Run it under WSL** — no downloads, works today:

```powershell
dotnet publish -c Release -r linux-x64 --self-contained true -o ./publish-linux
wsl -d Ubuntu -- bash -lc "cp -r ./publish-linux ~/probe && chmod +x ~/probe/WPR.Wp8Probe && ~/probe/WPR.Wp8Probe <exe>"
```

The self-contained publish pulls `libunicorn.so` out of the package for you.

**Supply `unicorn.dll`** — required eventually, since WPR ships on Windows. Either build
Unicorn from source with CMake + MSVC, or take the DLL from the official Unicorn Windows
release, and drop it next to the built executable.

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

Those members are now implemented rather than shaped: `get_Bounds` returns 800x480,
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
ID3D11DeviceContext1::RSSetViewports -> 800x480
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
              viewport=800x480 clear=(1.00,1.00,1.00,1.00)
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

The delegate was found at slot 3, invoked, and the game ran its handler - and then called
through a null at `0x0044F0EF`, on what the argument shape says is
`IWeakReference::Resolve(REFIID, IInspectable**)` against a weak reference that is null.

`IWeakReferenceSource` and `IWeakReference` are now implemented, because every C++/CX ref
class supports them and neither can be answered the way the other IIDs are: this probe's
WinRT `QueryInterface` hands back the same pointer for anything asked of it, which works only
while every interface has an `IInspectable` layout. These two do not - `GetWeakReference` and
`Resolve` both sit at **slot 3**, where an `IInspectable` has `GetIids` - so answering with
the original pointer aims both calls at the wrong method. They get real objects with an
IUnknown-shaped vtable, and resolving always succeeds, because nothing here ever dies and a
weak reference that resolves to null sends the image down its object-has-gone path.

It did not clear this particular null: the reference the handler resolves is null before it
gets there, so something earlier failed to hand one over. That is where input stands.

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
- **No exception unwinding.** `_CxxThrowException` stops the run rather than pretending.
  The `.pdata` unwind tables are all present; nothing reads them yet.
- **No real concurrency.** Deferred callbacks are not threads: anything expecting to run
  while its caller continues deadlocks rather than waits.
- **No `sscanf` or `strftime`.** The printf side of varargs works; scanning a string back
  into caller-supplied pointers does not. `CrtLibrary.NotImplemented` lists them.
- **D3D11 records, it does not render.** Device, context, DXGI chain, swap chain and the
  resource types exist, and every call is logged; nothing rasterises and no pixel is
  produced. Bridging emulated resources to a host GPU is untouched.
- **Nothing is rasterised.** A thousand frames are presented, but the D3D layer records
  calls rather than drawing: there is no pixel anywhere, only a description of the frame the
  game asked for.
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
- **Only one input event is ever raised.** A single tap at 240 frames, delivered from
  ProcessEvents. The other three subscriptions - Closed, PointerMoved, VisibilityChanged -
  are accepted and never fired, and there is no keyboard, no back button and no way to aim
  a tap anywhere but the middle of the screen.
- **The window size is a constant.** 800x480, chosen because every WP8 device shipped at
  WVGA or a scale of it. Nothing negotiates it with a real surface.
- **QueryInterface lies.** It returns the same object for any IID asked for, which holds
  only because these statics implement one interface each.
- **Unimplemented imports return 0.** Fine for a void or an ignored handle, a lie
  anywhere else. `HostStubs` covers only the startup path.
- **Single-threaded.** A Unicorn context is one thread; the WinRT thread pool is not.

## Background

Full feasibility study, including why running the ARM code natively is not an option on
either Windows or Android any more:
<https://claude.ai/code/artifact/e49413ae-2e98-4368-84c0-69262cae134f>
