# Ubisoft Spark2 (AC Pirates) — Managed Reimplementation: Feasibility & Design Scope

**Status:** Scoping study (no implementation).
**Verdict:** **Not practically feasible** as a managed reimplementation. It is gated on
reverse-engineering AES-encrypted content from non-runnable ARM binaries, and behind that
gate sits a full proprietary D3D11 3D engine (tens of thousands of API symbols). Recommended
action is a small, bounded *decryption spike* (below) as a go/no-go — expect it to say "no-go."

---

## 1. Why we're here

AC Pirates (`SparkApplicationXaml`, product `40ae56ef-…`) boots through WPR's Silverlight host
to `MainPage`, binds a DrawingSurface content provider, and WPR's D3D11 pipeline renders — but
the game's own content provider is `null` because its engine (`SparkApp.SparkAppBridge`) is a
winmd *stub*. The real renderer is **Ubisoft's Spark2 engine** (native). This doc scopes
reimplementing Spark2 in managed C# so the game could actually run.

## 2. What Spark2 actually is (evidence)

Spark2 is a **native C++, Lua-scripted, D3D11 3D engine** shipped as ~30 Lua binding modules.

| Fact | Evidence |
|---|---|
| Engine is **ARM-only** native code | PE machine = `ARMNT` for every engine module (`SparkSystem/Utils/ResourceManager`, `LuaGeeaEngine`, `LuaSpark2`, `LuaMotion`, `Lua`, `OMath`, …). Only x86 natives present are Win32 helper codecs (`binkw32`, `OpenAL32`) — **not** the engine. |
| **No managed/transpiled build** | Zero `llvm2cs` / `stonetrip` / `shiva` / `hermes` / `indigen` markers. (Contrast: Babel Rising 3D works because its vendor cross-compiled the engine to C# via `llvm2cs`; Ubisoft did not.) |
| Renderer is **D3D11, shader-based** | `AVgeD3D11Renderer`, `AVgeD3D11Shader`, `AVgeD3D11Texture`, `AVgeD3D11MultiRenderTarget`, shader-parameter types (camera pos, near/far clip, active lights…). |
| Proprietary **asset formats** | `AVGeeaGeometryFormat`, `AVGeeaMaterialFormat`, `AVGeeaDdsTextureFormat`, `AVGeeaPvrtcTextureFormat`. |
| **Huge API surface** | Distinct identifier tokens per module: `LuaMotion` ~2,028, `LuaGeeaEngine` ~1,950, `LuaSpark2` ~1,523, `LuaBox2D` ~654 — across ~30 modules (`LuaGeeaEngine`, `LuaSpark2`, `LuaMotion`, `LuaBox2D`, `LuaFreetype`, `LuaGeeaSoundEngine`, `LuaCSV`, `LuaJSon`, `LuaDevice`, `LuaSave`, `LuaWinrtInput`, `LuaEdgeAnimation`, `LuaBink`, `LuaMobileSDK`, and Collada/Obj/Geom/Png/Jpg/Tga/Wave/JSON format parsers). |
| Game content is **encrypted + compressed** | `package.zip` (925 MB) holds the project under `Spark2Projects/ACPirates_Demo/`. Directory names are readable (`Core/3C/Camera/CutScene`, `Common/EventManager`, `Localization`), but every file is a `.spd` with an **obfuscated name** (`Wbugfs…` ≈ `Camera…`) and **high-entropy content** (no headers/strings). `SparkResourceManager.dll` contains `aes`/`encrypt`/`decrypt`/`.spd`; `SparkSystem.dll` contains `zlib`/`inflate`/`deflate`. → `.spd` = **AES-encrypted, zlib-compressed**. |
| Bootstrap | `commandline.txt`: `ACPirates_Demo/Project_WP8_Launcher.def ./Spark2Projects/` — the engine loads a project `.def` (itself packed) and runs the game's Lua. |

## 3. Closed paths (for the record)

- **Run the native engine** — impossible: ARM machine code can't execute on x64; there is no
  x86 engine build in the package.
- **Emulate ARM** — hosting the WinRT component + WP8 D3D11 interop + Lua VM under an ARM→x64
  emulator is *building a WP8 emulator*. Out of scope; research-scale.
- **`llvm2cs`-transpile the ARM binary** — llvm2cs consumes LLVM bitcode/source at build time
  (what the vendor had). We only have stripped ARM binaries; there is nothing to transpile.

## 4. What a managed reimplementation would require

```
 game Lua (.spd, AES+zlib, obfuscated)  ──decrypt/decompress──►  Lua source/bytecode
                                                                      │
   managed Lua VM (MoonSharp / KeraLua) ◄────── runs game logic ──────┘
        │  calls the reimplemented engine API (the ~30 Lua modules)
        ▼
   Geea renderer (reimpl on Vortice D3D11 — WPR already ships it)  ──► WPR DrawingSurface
   + Motion/animation, Box2D physics, Freetype text, OpenAL/FNA sound, WinRT input, Save
        │  consumes decoded assets
        ▼
   asset pipeline: reverse Geea geometry/material/DDS/PVRTC formats
```

Component-by-component feasibility:

| Component | Approach | Difficulty |
|---|---|---|
| Lua VM | Drop in MoonSharp (pure C#) or KeraLua (native Lua) | **Low** — solved problem |
| `.spd` decrypt/decompress | Reverse AES key/IV + container layout from `SparkResourceManager` ARM disasm; zlib is standard | **Very high / uncertain** — the gate |
| Geea renderer API | Reimplement the Lua-facing render API on Vortice D3D11 | **Very high** — full shader engine, ~2k symbols |
| Asset formats (geom/material/DDS/PVRTC) | Reverse each proprietary format | **High** — several formats, no docs |
| Motion / Box2D / Freetype / sound / input / save | Map to managed equivalents (Box2D.NET, a managed FreeType, FNA audio, WPR input) | **Medium–High**, per module |
| Game Lua logic | Runs on the above once decrypted — but names are obfuscated | **Medium** (only after the gate) |

## 5. The three hard gates (in order)

1. **G1 — Content decryption (the wall).** Nothing downstream can be *validated* until we can
   read a single `.spd`. The AES key derivation is inside ARM-only binaries we cannot run, so
   it must come from **static ARM disassembly** (Ghidra/IDA). Ubisoft may use a fixed embedded
   key (tractable) or per-file/anti-tamper derivation (potentially intractable). Unknown until
   spiked.
2. **G2 — Engine API scale.** Even a single-game subset means reimplementing a large fraction of
   a shader-based D3D11 engine's Lua API, discovered by tracing which calls the (decrypted,
   obfuscated) Lua makes — a long tail.
3. **G3 — Proprietary asset formats.** Geometry, materials, DDS/PVRTC textures — each reversed
   from binaries.
4. **Legal/ethical.** G1 is circumventing a commercial game's content protection. This needs an
   explicit call from the project owner before any work; it likely limits this to private,
   non-distributed research at best.

## 6. Recommended first step: a bounded decryption spike (go/no-go)

Do **not** start engine work. Do the cheapest experiment that resolves G1:

> **Spike (time-boxed, ~1–2 weeks):** In Ghidra/IDA, disassemble `SparkResourceManager.dll`
> (ARM). Find the `.spd` open/read path, identify the AES mode + key/IV source and the
> zlib step and container header. Reproduce it in a throwaway C# tool and **decrypt one `.spd`
> into a readable Lua script or asset.**
>
> - **Success** → the project is *conceivable*; proceed to scope G2/G3 against real decrypted Lua.
> - **Blocked/intractable** (keyless-derivation, anti-tamper, per-file keys we can't reproduce)
>   → **stop.** The reimplementation is dead regardless of engine effort.

Kill criterion: if the spike can't produce one readable decrypted file in its time box, abandon.

## 7. Effort & risk

- **Realistic effort if all gates pass:** multi-person, **many months to years** — this is
  reversing and reimplementing a proprietary AAA mobile engine, not shimming.
- **Probability of reaching playable AC Pirates:** low. The G1 gate alone may end it; G2/G3 are
  each large; the whole is high-variance.
- **Best-case partial value:** the `.spd` reversal + a managed Lua VM + a minimal Geea render
  subset could, in principle, generalize to *other* native-Spark2 titles — but only if G1 is a
  fixed key, and only after very large investment.

## 8. Recommendation

Treat AC Pirates (and native-ARM Spark2 titles generally) as **out of scope for playable
bring-up.** The tractable engine wins in this catalogue are the **`llvm2cs`-transpiled ShiVa
games** (managed C#, e.g. Babel) — that's where "get the engine rendering" pays off. If there's
appetite to *explore* Spark2 anyway, fund only the **G1 decryption spike** as a go/no-go and
decide from its result. Meanwhile, AC Pirates can be left at its current state — it launches
cleanly to the shell (see `ac-pirates-launch-progress`), which is as far as it can go without
the engine.
