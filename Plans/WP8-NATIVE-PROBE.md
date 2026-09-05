# WP8 "Modern Native" titles — the ARM probe

**Status:** Research probe, working, **not wired into WPR and not shippable as it stands.**
**Verdict so far:** A WP8 native (C++/DirectX) title **can** be hosted in-process after all —
the probe boots Angry Birds Rio from cold to its main menu, episode list and level select, and
drives all three with injected touch. Two things stand between that and a playable game, and
both are understood: **one bug in the game's own Lua** blocks level entry, and **the CPU is 30×
slower than it needs to be** for a reason that is measured, not guessed.

Everything below is reproducible from `Research/Wp8Native/` — see its README, which is the long
form of this document. This file is the continuation plan.

---

## 1. Where it is

| | |
|---|---|
| Code | `Research/Wp8Native/` (console probe) + `Research/Wp8Native/Desktop/` (windowed host) |
| Long-form notes | `Research/Wp8Native/README.md` — every finding, with the evidence that produced it |
| Test subject | Angry Birds Rio 2.2.0.0, unpacked to `C:\wp8-test\abrio` (re-extract from the XAP in `Documents\Windows Phone Games`) |
| CPU | Unicorn 2.1.3 via `UnicornEngine.Unicorn`; `unicorn.dll` is a MinGW build, gitignored, and **must not be committed** |

Run the console probe with a screenshot contact sheet:

```powershell
.\run.ps1 -Game C:\wp8-test\abrio\AngryBirdsRio.exe -Screenshot .\frame.png -Frame 1200
```

Run the window (click = tap, drag = drag; the title bar shows frames, fps and the last tap):

```powershell
.\run.ps1 -Desktop -Game C:\wp8-test\abrio\AngryBirdsRio.exe
```

## 2. What works today

Reached by scripted touch alone, cold start to level select — `menu-clean.png`,
`episode-select.png`, `level-select.png` and `playground-page.png` in the probe directory are
the frames:

- Boot, CRT static init, Lua bootstrap, the three splash screens, the title screen.
- The Xbox LIVE stack — five interfaces implemented from `Microsoft.Xbox.winmd`, which ships
  inside the XAP. Sign-in, achievements and leaderboards all complete their async callbacks.
- The main menu, its error dialog, PLAY, the episode list, an episode's level page, the
  Playground page.
- **Touch input**: taps and drags, delivered one pointer event per turn round the main loop.
  Drag is proved by page-turning the level scroller and returning to a byte-identical frame.
- Audio, files, isolated storage, and D3D11 through a software rasteriser that produces the
  PNGs.

The run ends with self-tests that must stay green: the vtable bridge, the callback bridge, and
(under WSL) the whole probe at 3/3 PASS.

## 3. The blocker to gameplay

Tapping any level tile — in an episode **or** in Playground — stops the run:

```
stopped   the image threw .?AVLuaException@lua@@; unwound 3 frames, no matching catch found
message: "bad argument #1 to 'pairs' (table expected, got nil)"
message: " (call stack not available)"
```

The site is named by the game's own recovered scripts. `PageGrid` has exactly four methods and
one of them is `getPage`, which does `pairs(self.pages)`; `self.pages` is created in
`PageGrid:init` and nowhere else. **A page grid whose init never ran was asked for a page.**

The same missing initialisation is the likely reason the level page builds **three tiles per
page across two pages** where the pack on disk has fifteen (`assets/data/levels/airport1` and
its siblings), and why the column it does build sits about 150px too high.

**This is the image's own code failing, not the emulator refusing to run it** — no
unimplemented import, no stubbed vtable slot, no fault, and the CPU executed every instruction
the game asked for. The next step is to follow the menu engine's scene construction in the
recovered Lua and find what skips that init. The scripts are recoverable at will:
`WPR_DUMPLUA=<dir>` hooks `free` and catches all 109 of them, and the scratch `luac.py`
disassembles Lua 5.1 with constants resolved.

## 4. Performance — the case for dynarmic, measured

The load takes about 139 seconds: 10.7s before the first frame, then **302 frames of loading at
424ms each**. The report prints that timeline itself now (`HOW THE LOAD SPENT ITS TIME`).

What it is **not**:

| Suspect | Measured |
|---|---|
| Host stubs (our C#) | **2%** of the run — all of them together |
| Boundary crossings | **0.16µs** each; 3.7M of them is half a second |
| `memcmp` + `strcmp` (43% of all crossings) | Rewriting them in guest code would save almost nothing |
| Block chaining lost to code hooks | **1.24×** on realistic code, not the 4.7× a tight-loop benchmark implies |

What it is: **guest stores**, about 96ns each — 25× a load, 70× an ALU operation — which is
Unicorn's write path, not anything WPR does. The image is 11% stores (210,097,414 counted in
two billion instructions).

| loop | Unicorn | dynarmic | |
|---|---|---|---|
| tight ALU | 788 MIPS | 3,384 MIPS | 4.3× |
| realistic mix (load, add, store, call, branch) | **57 MIPS** | **1,794 MIPS** | **31×** |

dynarmic's number is on its *slow* memory path (`UserCallbacks`, a virtual call per access); a
page table would be faster again. 31× turns the 128-second load into about four seconds —
roughly what the phone did.

### 4a. The shim exists and is proven (2026-09-04)

`~/wprcpu/` in WSL holds `wprcpu.h` + `shim.cpp`: a C ABI over dynarmic's A32 JIT, built to
`wprcpu.dll` (static mingw runtime, imports only `KERNEL32`/`msvcrt`), copied to
`Research/Wp8Native/wprcpu.dll` (gitignored beside `unicorn.dll`) and wrapped by
`DynarmicNative.cs` / `DynarmicCpu.cs`. `WPR_DYNPROBE=1` runs its proofs at the end of any
probe run, in the same process as the Unicorn benchmarks:

```
dynarmic trap    PASS: fired 1x at pc 0xA0000002 (slot 0xA0000000), r0=7, r3=42
trap page write  PASS: refused=True outcome=2
lazy page        PASS: handler called 1x, r0=5, mapped=True
mixed, dynarmic  40,000,000 instruction(s) in 0.02s = 2047 MIPS   (Unicorn, same process: 45-58)
```

What those settle, in order: **the trap mechanism** is `svc #0 ; bx r12` per slot with the
handler running at the `svc` (registers exact, block ended) and the `bx r12` still doing the
tail call - the exact shape Unicorn's MinGW build could not survive. **The trap page** is
mapped read+execute, so a stray store into it is a fault delivered to C#, not a corrupted slot.
**Lazy mapping** works as it does under Unicorn: the unmapped handler maps and returns true,
the access retries transparently. Memory the JIT may touch alone is RWX and unwatched; the
trap page, null page and unmapped space go through callbacks where C# decides. CP15 c13
(TPIDRURO/TPIDRURW) is a real coprocessor, so the TEB pointer will actually work.

What is **not** built yet is the other side: `ArmEmulator` still talks to Unicorn directly. The
port is a seam (`IArmCpu`) with two implementations; the Unicorn-touchpoint inventory (41
touchpoints, design workflow `wf_3f942e96-f8f`) is the map for that rewrite, and its hardest
parts are known: no per-instruction hooks (WPR_TRACE, the block sampler and the heap-execution
guard need substitutes or degrade to "unsupported"), stale registers inside memory callbacks,
and byte-identical frame comparison as the regression yardstick between engines.

## 5. Licensing — this decides itself

**Unicorn is GPLv2. WPR is MIT.** The probe cannot ship on its current CPU at all, whatever its
performance. **dynarmic is 0BSD**, and is also the answer to section 4. So the port is not a
trade between legality and speed — it is the single change that delivers both. See the
`unicorn-is-gpl-wpr-is-mit` memory.

The prototype exists and is built: `~/dyn` (dynarmic) and `~/dynbench` (both benchmarks) under
WSL.

**Cross-compiled for Windows on 2026-09-04, and proven there.** This machine has no C++
compiler of its own, so the route is WSL's `mingw-w64` (installed that day) building a Windows
x64 static library and, later, the shim DLL. The recipe that works:

```
# toolchain file ~/mingw.cmake: CMAKE_SYSTEM_NAME Windows, x86_64-w64-mingw32-g++,
# CMAKE_FIND_ROOT_PATH_MODE_INCLUDE BOTH
cmake -S ~/dyn -B ~/dyn/build-win -DCMAKE_TOOLCHAIN_FILE=$HOME/mingw.cmake \
      -DCMAKE_BUILD_TYPE=Release -DDYNARMIC_USE_BUNDLED_EXTERNALS=ON -DDYNARMIC_TESTS=OFF \
      -DDYNARMIC_WARNINGS_AS_ERRORS=OFF -DDYNARMIC_USE_PRECOMPILED_HEADERS=OFF \
      -DBoost_INCLUDE_DIR=$HOME/boost-include
cmake --build ~/dyn/build-win -j 8 --target dynarmic
```

Two things that cost a build each: **`Boost_INCLUDE_DIR` must not be `/usr/include`** - that
puts glibc's `stdint.h` ahead of mingw's and every file fails on
`bits/libc-header-start.h`; `~/boost-include` holds a single symlink to `/usr/include/boost` so
only Boost is visible. And WSL's `/tmp` is wiped when the VM idles out, so logs go under `~`.

Link with `-static -static-libgcc -static-libstdc++` against `libdynarmic.a libmcl.a libfmt.a
libZydis.a libZycore.a`; the result imports only `KERNEL32.dll` and `msvcrt.dll`, and .NET
P/Invoke into it was verified. The mixed-loop benchmark built this way runs **on Windows** at
**~1,700 MIPS**, against Unicorn's 46-57 on the same machine and loop.

## 6. Known graphics defects

- **Fixed 2026-09-03: 16-bit texture formats.** Every uncompressed texture was sized at four
  bytes per pixel, so a `B4G4R4A4` one (DXGI format 115 — what a phone game stores its UI in,
  to halve the memory) had each row written at twice its true stride with half of it dropped,
  then read back as RGBA. The symptom was a 38-pixel band of horizontal stripes down the right
  of the episode list; the truth was that the whole right-hand foreground — a tree, its leaves
  and a flower — was never drawn. `Resource.PixelBytes` now carries the size and
  `FrameCapture.Sample` decodes formats 85, 86 and 115. Channels are *expanded*, not shifted,
  or every white in the UI comes out grey.
  **Expect this to have fixed more than one screen**: a WP title draws most of its UI from
  4444 atlases.
- Texture addressing is still clamp-only, which was the first guess here and was wrong. It has
  not yet cost anything visible — every draw checked has UVs inside [0,1] — but a tiled
  background would smear the edge texel rather than repeat.
- Further artefacts were reported but not captured before the window closed. **Get a screenshot
  of each before guessing** — this one was found from the report's per-draw dump (screen
  coordinates, UVs, texture size and *format* per draw), not by reasoning about the picture.

## 6a. Converting the ARM at install time

Asked 2026-09-03: could the translation happen once, at install, so a launch is cheap? That is
static binary translation, and **this image is unusually well suited to it** — three facts, all
already measured rather than assumed:

| Fact | Why it matters | Evidence |
|---|---|---|
| The image carries a **complete function table** | Code discovery — finding where functions begin — is the usual thing that sinks static translation. An ARM PE ships `.pdata` for exception unwinding, so the file hands us the inventory. | **7,671 entries**, 3,554 packed + 4,117 xdata, printed every run |
| It **never executes generated code** | Nothing invalidates a translation mid-run. Its Lua is 5.1, an interpreter, not LuaJIT. | The heap-execution guard has never fired in any run; `lazy pages 1` |
| `.text` is **1.59 MB** | Small enough to translate whole, not just hot paths. | The IMAGE section of the report |

**It would fix the right thing.** The measured bottleneck is guest stores at ~107ns, which is
Unicorn's software MMU. A translator reserves a 4GB host region once and makes a guest store
`base + offset` with no bounds check — one instruction. That is the whole gap.

**What it would have to get right**, in rough order of how much each decides the outcome:

- **Lazy flags.** Computing NZCV after every instruction costs more than everything else put
  together. Flags must be computed only where something reads them, which means tracking the
  pending operation through the block.
- **Indirect branches.** A C++ game calls through vtables constantly, so a translated function
  cannot be a closed graph. The standard answer works here: a dispatch table from guest address
  to translated method, and `.pdata` bounds the set of legal targets.
- **The memory model** — the 4GB reservation above, which is what buys the speed.
- **IT blocks, conditional execution and VFP** — tedious rather than hard.
- **C++ exceptions.** The probe's unwinder walks *guest* frames using the image's own `.pdata`,
  so translated code has to keep guest SP and frame layout observable, not just the results.

**Emit C#, not machine code.** Translating to IL and compiling with Roslyn at install time
needs no native toolchain (this machine has neither MSVC nor cmake), keeps everything MIT, and
fits the shape WPR already has — `ApplicationPatcher` rewrites IL per game at install and
versions the result. The CLR's own JIT then does register allocation.

**But do dynarmic first.** An AOT translator is a much larger project than wiring in a JIT that
already exists, is already built here, and already measures **31x** on the realistic mix. Most
of an AOT translator's advantage over a good JIT is compile time, not steady-state speed — and
dynarmic already removes the store cost, which is the entire measured problem. If AOT is wanted
afterwards, the block profiler already says where it would pay: **one function is 29.8% of guest
time** (`0x0046C649`, 173 blocks).

### The cheaper install-time idea, which targets the same complaint

The load does **the same work every launch**: 10.7s of static initialisers, then 302 frames of
decompressing and script compilation. Nothing about it depends on the user.

So do it once — at install — and **snapshot the emulated machine** at the menu: guest memory,
registers, and the trap tables. A launch then restores and runs.

The hard part is not the guest, it is **our own side**: the D3D resources, open file handles,
WinRT stand-ins, the HString heap and the allocator's bookkeeping all live in C# objects that a
guest-memory dump knows nothing about. They would have to be serialised alongside, or rebuilt
deterministically from a replay of the calls that made them.

That is far less work than an ARM-to-IL compiler and it removes the two minutes entirely rather
than making them 30x cheaper. **It is the first thing to try if the goal is "starts quickly"
rather than "runs quickly"** — and the two are different problems with different answers.

## 7. Instruments already built

Reach for these before adding print statements — each exists because a guess cost a day.

| Knob | What it gives |
|---|---|
| `WPR_INPUT` | Gesture script: `tap:x,y`, `drag:x1,y1>x2,y2@n`, `wait:n`. With `WPR_TAP` as the period, a script is a timeline through the menus. |
| `WPR_SCREENSHOT=path:frame+every` | A contact sheet instead of one photograph |
| `WPR_DUMPLUA=dir` | Every Lua chunk the game loads, caught at `free` |
| `WPR_SAMPLE`, `WPR_ARGS` | PC sampling, and argument capture at one call site |
| `WPR_SLOTS` | Which vtable slots the image called on a stand-in |
| `WPR_CLOCK` | Virtual clock rate. **Does not help the load** — that is compute, not waiting — kept because the negative result is worth keeping. |
| Report sections | `HOW THE LOAD SPENT ITS TIME`, `TIME INSIDE HOST STUBS`, `WHERE THE TIME WENT`, and the `guest stores` count |

## 8. Order of work when this resumes

1. **The Lua init bug** — the cheapest path to gameplay, and gameplay is what proves the whole
   approach.
2. **The dynarmic port** — required for shipping (licence) and worth 31×. Do it after (1), so
   there is a known-good reference to compare against instruction for instruction.
3. Texture wrap in the rasteriser, plus whatever the outstanding artefacts turn out to be.
4. Only then: how this is surfaced in WPR at all — a launcher rail like the Unity one, or
   in-process hosting. **Nothing about this is wired into WPR today, and nothing should be
   until 1 and 2 land.**

## 9. Before it goes near a release

- `Research/Wp8Native/` is **untracked**. Decide deliberately whether it is committed; if it
  is, note that the PNGs are about 1.5 MB each.
- `unicorn.dll` (21 MB, GPL) is gitignored. **Keep it that way.**
- The probe has no product surface: it is in no solution filter, no head references it, and
  `BackendIsolationTests` does not scan it. Nothing here can affect a WPR build today.
