# WP8 native probe — where the work is and how to pick it up

The WP8 "Modern Native" (ARM) research lives on its own branch in a worktree so `main` stays
clear. Nothing on that branch is wired into WPR; it is a standalone probe under
`Research/Wp8Native/`.

| | |
|---|---|
| Worktree | `.claude/worktrees/wp8-native` (under this repo) |
| Branch | `wp8-native`, branched from `2f35d69e` (main) |
| Last commit | `becd2e4f` — *wip: WP8 native probe - dynarmic engine seam, Lua traceback, plan notes* |
| Full plan | `Plans/WP8-NATIVE-PROBE.md` **on that branch** — measurements, evidence, order of work |
| Long-form notes | `Research/Wp8Native/README.md` on that branch |

## Resume in one minute

```powershell
cd .claude\worktrees\wp8-native\Research\Wp8Native
.\run.ps1 -Desktop -Game C:\wp8-test\abrio\AngryBirdsRio.exe      # the window (defaults to dynarmic)
.\run.ps1 -Game C:\wp8-test\abrio\AngryBirdsRio.exe -Screenshot .\f.png -Frame 300   # console + PNG
```

`WPR_CPU=unicorn` selects the reference engine; the report's `cpu` line says which ran.

Two native DLLs sit beside the sources and are **gitignored, per machine**: `unicorn.dll`
(GPL — never ship) and `wprcpu.dll` (the dynarmic shim, 0BSD). Both are already in the
worktree. To rebuild the shim: WSL, `~/wprcpu`, `cmake --build build`, copy `build/wprcpu.dll`
to `Research/Wp8Native/`. The shim source is versioned under `Research/Wp8Native/Native/` with
the mingw toolchain file; the recipe is in the plan's §5.

The test subject is Angry Birds Rio unpacked at `C:\wp8-test\abrio` — re-extract from
`Documents\Windows Phone Games\Angry Birds Rio v2.2.0.0.xap` if it is missing. The game's saved
state lives in `%TEMP%\wpr-wp8-sandbox`; **delete it before a run that must behave like a first
launch** (see below).

## State at the commit

**Working, on Unicorn:** cold boot to main menu, episode list and level select, all driven by
scripted touch; the engine seam (`IArmCpu`) is proven byte-identical at frame 300 before and
after.

**dynarmic:** the shim is proven (trap, trap-page protection, lazy mapping, ~2,000 MIPS from
C#), and the game gets through CRT init — 195 static initialisers, 2,070 import calls — but has
not yet presented a frame. That is the open thread. Compare against Unicorn with
`WPR_SCREENSHOT=...:300` on both and diff the PNG hashes; the Unicorn reference at frame 300 is
`22fef79f2de186622e93838aeec50282`.

**Level entry:** the `pairs(nil)` Lua error is understood. `WPR_LUATRACE=0x00471CE4` prints
the Lua call stack at `luaL_argerror`: `releaseAssets` is asked to release group
`INGAME_RIO0`, which was never loaded because chapter **0** is not a real chapter —
`defaultChapter` is 2. The 0 comes from a **stale saved `settings.lua`** in the sandbox written
by an earlier probe run. Deleting the sandbox and re-running to level entry is the pending
confirmation; if a fresh run still writes 0, the probe's number formatting in the settings
serialiser is the suspect.

## Next, in order

1. Confirm the first-run theory (sandbox deleted, run to level entry with `WPR_LUATRACE`).
2. Get dynarmic to the first frame — bracket with small budgets; the report's `final PC`,
   `final regs` and `trap at pc` lines were added for exactly this.
3. Byte-compare frame 300 across engines, then run the menu path on dynarmic.
4. Only then think about how WPR surfaces it (a launcher rail like Unity's, or in-process).

## Housekeeping

- `git worktree list` shows it; `git worktree remove .claude/worktrees/wp8-native` when done.
- The branch is local only. Push it if the work should survive this machine.
