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

## 5. Licensing — this decides itself

**Unicorn is GPLv2. WPR is MIT.** The probe cannot ship on its current CPU at all, whatever its
performance. **dynarmic is 0BSD**, and is also the answer to section 4. So the port is not a
trade between legality and speed — it is the single change that delivers both. See the
`unicorn-is-gpl-wpr-is-mit` memory.

The prototype exists and is built: `~/dyn` (dynarmic) and `~/dynbench` (both benchmarks) under
WSL.

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
