# Stage 5c scope — reimplementing the XNA runtime over an RHI seam

**Date:** 2026-08-07 · **Prereqs landed:** 5a (owned value/math types), 5b (pure-type
sweep — 134 public types now in `WPR.Framework.Xna`), Stage 4 (`WPR.Backend.FNA` +
`IGameHost`). · **Reads this scope is built on:** four read-only audits of the current
tree (FNA remnant, `WPR.Abstractions`, frame-to-screen path, `FNA3D.cs`).

## Where we actually are (vs the original 5-sizing plan)

5a+5b moved **far more** than the original audit's "5a = 36 math types" — every
*pure-managed* XNA type (enums, packed vectors, input value-structs, `GameTime`,
component interfaces, exceptions) is already WPR-owned. So the residue is clean:

**FNA now contains only native-backed runtime**, in five subsystems + one "spine":

| Subsystem | Native surface | ~LOC | Liftable? |
|---|---|---|---|
| **Graphics** | `FNA3D_*` (~153 P/Invokes in `FNA3D.cs`) | ~20k | Managed logic + native *leaves* → **yes, behind an RHI** |
| **Audio** | `FAudio.*` / `FACT*` (`FAudio.cs`) | ~6k | Managed logic + native leaves → **yes, behind an audio seam** |
| **Media** | `FAudio.XNA_*` songs + `Theorafile_*` video | ~1.8k | **yes** (audio + a video-decode seam) |
| **Input (devices)** | route through `FNAPlatform` → SDL | ~2.4k | **yes, behind an input seam** (value-structs already moved) |
| **Content** | almost entirely managed; native only in a few resource-building readers | ~5k | **yes — mostly a *move*** once Graphics/Audio seams exist |
| **The spine** | `Game`, `GameWindow`/`FNAWindow`, `GraphicsDeviceManager`, `FNAPlatform`/`SDL2_FNAPlatform` (741 SDL calls), window creation, event pump, main loop | ~15k | **structurally platform-bound — see the scope fork below** |

## The central design decision — the graphics seam is FNA3D-shaped

The current `WPR.Abstractions.Graphics.IGraphicsContext` is a coarse scaffold
(`Clear`/`Present`/`CreateTexture(bgra)`) — its own doc-comment says it "grows in
Stage 5c (draw calls, blend/depth state, vertex/index buffers)." Good: it was always
meant to become a real RHI. The audit shows exactly what that RHI should look like:

**`FNA3D` is already a clean, low-level RHI, and its C structs are already written in
the value types we own.** `FNA3D_BlendState`/`SamplerState`/`RasterizerState`/
`DepthStencilState`/`PresentationParameters` are composed of `Blend`, `BlendFunction`,
`SurfaceFormat`, `TextureFilter`, `TextureAddressMode`, `Color`, `DepthFormat`,
`PresentInterval` — **every one of which moved to `WPR.Framework.Xna` in 5b** — plus
`IntPtr` opaque handles for device/texture/buffer/effect/rendertarget.

⇒ **Recommended seam: a C# interface that mirrors the FNA3D C API almost 1:1**, expressed
in WPR-owned value types + opaque `IntPtr` handles. Not an object-oriented "resource"
abstraction. Rationale:

- The vocabulary already exists (the 5b enums/structs) — signatures need no new types.
- FNA's own `GraphicsDevice`/`Texture2D`/`Effect`/buffers are *thin managed wrappers over
  exactly these calls*, so lifting them is mechanical: replace each `FNA3D_Foo(...)` leaf
  with `_backend.Foo(...)`. ~90% of the managed logic (state caching in `PipelineCache`,
  `SpriteBatch` batching, `EffectParameter` marshalling, DXT decode) is preserved verbatim.
- A future `WPR.Backend.Direct3D11` implements the **same** interface over Vortice — a
  C-style handle RHI maps cleanly onto D3D11, and forces the abstraction to not be
  accidentally FNA-internal-shaped.
- Draw calls are coarse-grained (one call per `SpriteBatch` flush / primitive batch, not
  per sprite/vertex), so interface dispatch is free next to the P/Invoke + GPU cost.

**Seam location (corrected during 5c-0):** the RHI lives in **`WPR.Framework.Xna`**
(namespace `WPR.Xna.Rhi`), *not* `WPR.Abstractions`. The RHI vocabulary *is* the XNA enums
(`Blend`, `SurfaceFormat`, …), which live in `WPR.Framework.Xna`; putting the RHI in the
dependency-free `WPR.Abstractions` would force `Abstractions → WPR.Framework.Xna`, and since
`WPR.Framework.Xna` consumes the RHI that is a **cycle**. Any backend that renders XNA content
already references the XNA type system, so co-locating the contract with it is not a layering
violation — and it keeps `WPR.Abstractions` genuinely generic (host/window/lifecycle only).

Illustrative (not final) shape — `WPR.Xna.Rhi.IGraphicsBackend`:

```csharp
IntPtr CreateDevice(PresentationParameters pp, bool debugMode);
void   ResetBackbuffer(IntPtr device, PresentationParameters pp);
void   SwapBuffers(IntPtr device, Rectangle? src, Rectangle? dst, IntPtr overrideWindowHandle);
IntPtr CreateTexture2D(IntPtr device, SurfaceFormat fmt, int w, int h, int levels, bool isRenderTarget);
void   SetTextureData2D(IntPtr device, IntPtr tex, int x,int y,int w,int h, int level, IntPtr data, int len);
IntPtr GenVertexBuffer(IntPtr device, bool dynamic, BufferUsage usage, int sizeInBytes);
void   ApplyEffect(IntPtr device, IntPtr effect, uint pass, IntPtr stateChangesPtr);
void   DrawIndexedPrimitives(IntPtr device, PrimitiveType prim, int baseVertex, int minVertexIndex,
                             int numVertices, int startIndex, int primitiveCount, IntPtr indices, IndexElementSize sz);
void   VerifyPixelSampler(IntPtr device, int index, IntPtr texture, in FNA3DSamplerState state);
// …≈ the FNA3D.cs region set: Device / Presentation / Drawing / Render-states /
//    Render-targets / Textures / Buffers / Effects / Queries. ~60–80 members.
```

Audio (`IAudioBackend`) and video (`IVideoBackend`) follow the same pattern over
`FAudio`/`FACT` and `Theorafile`. Input rides the existing `IInputProvider` scaffold,
expanded to the `FNAPlatform` device surface.

## The directional inversion (and how the impl is injected)

Today FNA *defines* `GraphicsDevice`; after 5c `WPR.Framework.Xna` defines it and FNA
*implements the RHI it calls*. Concretely:

- `WPR.Framework.Xna` owns the RHI seam (see correction above) and its
  `GraphicsDevice`/`SpriteBatch`/… call `IGraphicsBackend`. It references **nothing** extra
  (no FNA, no WPR.Abstractions) — stays a dependency-free, fitness-clean leaf.
- `WPR.Backend.FNA` (already references both `WPR.Framework.Xna` and `WPR.Abstractions`)
  gains `FnaGraphicsBackend : IGraphicsBackend` etc., which P/Invoke `FNA3D`/`FAudio`.
  The `FNA3D.cs`/`FAudio.cs`/`SDL2.cs` bindings move here (or the fna source-tree stays as
  the backend's private native layer — mechanically simplest to leave `Src/ThirdParty/fna`
  in place and let the backend reference it). **Superseded after 5c-5:** the tree moved to
  `Src/Backends/FNA.Platform/`, beside its only consumer — see "Tree relocation" at the end.
- **Injection** mirrors the existing `FNAPlatform` static-delegate-table pattern, inverted:
  at `FnaGameHost.RunAsync` startup the backend registers its impls into a
  `WPR.Framework.Xna` backend registry (a settable static / tiny service locator) **before**
  the game's `GraphicsDevice` is constructed, and **clears it on teardown** (see Risk #1).

The window handle still flows in as an `IntPtr`: the spine (which keeps window ownership,
below) sets `PresentationParameters.DeviceWindowHandle`; WPR's `GraphicsDevice` passes it
straight to `IGraphicsBackend.CreateDevice`. The split is viable because the handle is opaque.

## Scope fork — does 5c include "the spine"?

`Game` / `GameWindow` / `GraphicsDeviceManager` / `FNAPlatform`+`SDL2_FNAPlatform` are the
game loop, window creation, and the SDL event pump. Two structural facts make them a
different kind of work from the resource lift:

1. **FNA renders into its own top-level SDL window — there is no Avalonia bridge for the
   game path** (confirmed: zero `D3D11Image`/shared-texture/`SetParent` anywhere near it;
   Avalonia is only the launcher shell). Moving window ownership into WPR means *building*
   that bridge (shared surface / composited present), which does not exist today. That's a
   **product** question (keep the separate game window? composite into the shell?), not a refactor.
2. The teardown ordering (Risk #1) lives in the spine's `ApplicationLaunch` finally-ladder.

**Recommended boundary: 5c lifts the *type system* (Graphics/Audio/Media/Content/Input)
behind the seams and LEAVES the spine in `WPR.Backend.FNA`.** FNA keeps owning the SDL
window + loop + event pump; WPR owns every resource/device/content/audio type the games
actually call. This severs ~35k of the ~50k residual LOC, gets the entire XNA graphics/audio
type system WPR-owned and fitness-clean, and does **not** entangle 5c with the unsolved
window-compositing problem. The spine (and the "own the window / present into the shell"
decision) becomes a distinct later stage.

Trade-off: with the spine left in FNA, `Microsoft.Xna.Framework.Game` remains a
*backend-defined* game-facing identity (patcher rescopes game `Game` refs to FNA, as today).
Defensible — `Game` is inseparable from the platform loop — but it means "games bind only
WPR-owned identities" is not 100% true until the spine stage.

## Recommended decomposition (each sub-stage ends green + is reinstall-forcing)

Every step that moves a type out of FNA follows the **established no-redirect contract**
(memory: `architecture-migration-plan`): add the moved FullNames to
`ApplicationPatcher.WprFrameworkXnaTypes`, keep `module.AssemblyReferences.Add`, bump
`ApplicationPatcher.Version`, **reinstall all games**, smoke-test.

- **5c-0 — Seam design + injection plumbing (no type moves yet). ✅ LANDED 2026-08-07.**
  Built the graphics RHI: `WPR.Xna.Rhi.IGraphicsBackend` (~70 members mirroring FNA3D's
  regions) + layout-identical `Rhi*` marshalling structs + the `XnaBackend` injection registry,
  all in `WPR.Framework.Xna` (which still references nothing). `WPR.Backend.FNA.FnaGraphicsBackend`
  implements it, forwarding to FNA's internal `FNA3D` via a new `[InternalsVisibleTo("WPR.Backend.FNA")]`
  on FNA (so FNA's DllImport resolver fires); registered/cleared in `FnaGameHost.RunAsync`.
  **No game-facing change, no Version bump, no reinstall.** Verified: `WPR.Framework.Xna`,
  `WPR.Backend.FNA`, `WPR.Runtime` all build 0 errors — the full delegating adapter compiling
  against the real FNA3D surface *is* the proof the ~70-member interface is a faithful, complete mirror.
  **Deviation from the original 5c-0 plan:** `IAudioBackend`/`IVideoBackend`/`IInputProvider` were
  **deferred to their own phases** (5c-3/5c-5). Rationale: unlike graphics (a closed ~70-entry FNA3D
  set), the audio/video/input native surface is larger and messier and its exact seam shape depends on
  how each type lifts — building it speculatively now would churn. The graphics RHI fully establishes
  the injection pattern each will follow.
- **5c-0b — Removed the XnaFacades project. ✅ LANDED 2026-08-07** (done alongside 5c-0, user request).
  Deleted the 10 pure-`TypeForwardedTo` shim projects (`Src/Core/XnaFacades/`) + their sln entries;
  dropped their `ProjectReference`s from `WPR.Runtime` and `WPR.Backend.FNA`, and `WPR.Runtime`'s now-dead
  direct FNA ref too (Runtime is FNA-free). Safe because the patcher rescopes games to FNA/`WPR.Framework.Xna`
  directly, so the facades had no runtime consumer. Fitness baseline: dropped the 8 facades **and** `WPR.Runtime`.
  `Microsoft.Xna.Framework.GamerServices` is a real impl (not a facade) and stays.
- **5c-1 — The whole graphics namespace. ✅ LANDED 2026-08-07 (Version 6, reinstall-forcing).**
  Dependency analysis proved there is no smaller slice (every game-facing graphics type derives from
  `GraphicsResource`, which holds a `GraphicsDevice`), so all 67 `Graphics/**` files (except the
  `FNA3D.cs` native binding) + `WprDebugTrace` moved into `WPR.Framework.Xna` in one push. The ~100
  `FNA3D.FNA3D_*` calls were rewritten to `XnaBackend.Graphics.*` (RHI uses `byte` to match FNA3D's ABI,
  making the rewrite a mechanical `sed`); the `FNA3D_*` struct fields became the 5c-0 `Rhi*` structs.
  Three seam touchpoints handled: image load/save + adapter enumeration (added to the RHI), logger + the
  Mouse/TouchPanel backbuffer back-ref (hooks on `XnaBackend`). `VideoPlayer` (stays in FNA) got the same
  rewrite since it drove graphics directly. Patcher `WprFrameworkXnaTypes` 134→197 (regenerated from the
  built DLL); `WPR.XnaCompability`'s `GraphicsDevice2`/`GraphicsAdapter2` subclasses recompiled unchanged.
  Build-verified across `WPR.Framework.Xna` + FNA + `WPR.XnaCompability` + `WPR.Backend.FNA` + `WPR.Loader`.
  A WPR-owned `FNA3D` shim was ruled out: FNA's IVT to `WPR.Framework.Xna` would make a same-named class
  collide inside FNA — so the moved code calls the RHI directly (matching the "minimize shims" principle).
- **5c-2 — `GraphicsDevice` + `SpriteBatch` + `Effect`/stock effects + `SpriteFont` + `Model`.**
  The core render path and the biggest chunk. Reinstall + smoke (this is where a math/batch
  bug would show as wrong rendering everywhere — Risk #2).
- **5c-3 — Audio + Media.** Re-planned 2026-08-08 after a dependency/surface audit; the original
  "one step, mirror the C ABI like graphics" shape does **not** work here. Three findings:

  1. **The native surface is ~2× graphics and not ABI-mirror-friendly.** 232 `FAudio.*`/`FACT*`
     call sites and **21 structs/enums**, several carrying delegates or pointer-arrays
     (`FACTRuntimeParameters` notification + file-IO callbacks, `FACTNotificationDescription`,
     `F3DAUDIO_EMITTER/LISTENER/DSP_SETTINGS`). Mirroring those means marshalling delegates
     across the seam with GC-lifetime hazards — exactly the wrong thing to hand-roll in the
     historically fragile audio path.
     ⇒ **DESIGN CHANGE: the audio seam sits slightly HIGHER than the C ABI.** Instead of
     `CreateSourceVoice(ref FAudioWaveFormatEx …)`, the interface takes primitives
     (`channels/sampleRate/bits/…`) and the **adapter** builds the native structs. This collapses
     ~all 14 FAudio structs out of the contract — e.g. the whole 3D calculation becomes one
     `Apply3D(listener…, emitter…, out coefficients, out doppler)` op instead of exposing three
     F3DAUDIO structs. Audio calls are per-sound-instance, not per-frame, so the extra
     indirection costs nothing at runtime, and a future XAudio2/AAudio backend implements the
     same ops rather than emulating FAudio's ABI.
  2. **`FrameworkDispatcher` is a dependency knot** (`fna/src/FrameworkDispatcher.cs:32-73`): its
     one `Update()` touches `DynamicSoundEffectInstance` + `Microphone` (audio), `MediaPlayer`
     (media) **and** `TouchPanel` (input). So it can only move after all three subsystems do, yet
     `SoundEffectInstance`/`DynamicSoundEffectInstance` reference `FrameworkDispatcher.Streams` —
     moving them while it stays in FNA would be a `WPR.Framework.Xna → FNA` cycle.
     ⇒ **Fix = invert the pump**: the stream registry moves to the audio side with an internal
     `UpdateAll()`, and FNA's `FrameworkDispatcher.Update()` calls *into* it (legal via the
     existing `[InternalsVisibleTo("FNA")]`). `FrameworkDispatcher` itself stays in FNA until
     5c-5, then moves.
  3. **Risk #1 teardown is safe.** `ApplicationLaunch.TeardownAudioState` (`:741-779`) resolves
     via compile-time `typeof(MediaPlayer)`/`typeof(SoundEffect)`, **not** assembly-qualified
     strings, so it re-binds after the move. Its member-level reflection
     (`GetNestedType("FAudioContext")` → static field `Context`, `MediaPlayer.DisposeIfNecessary`)
     survives **only if the move is verbatim** — those names and their visibility must not change.

  **Revised sub-steps** (each ends green, each reinstall-forcing):
  - **5c-3a — FAudio SFX core. ✅ LANDED 2026-08-08 (Version 7, reinstall-forcing).** All six types moved;
    FNA keeps only XACT. `SoundEffect.FAudioContext` became a thin shim over the backend (its reflective
    shape preserved for *two* callers — the host teardown and FNA's `SDL2_FNAPlatform.ProgramExit`);
    `SoundEffectInstance`'s `F3DAUDIO_DSP_SETTINGS` + unmanaged matrix became managed state;
    `AudioListener`/`AudioEmitter` became plain `Vector3` properties with the right/left-handed **Z flip**
    and the fixed emitter defaults moved into the backend; the `FrameworkDispatcher` pump was inverted onto
    `DynamicSoundEffectInstance.UpdateAll()`. A temporary FNA-side helper (`src/Audio/WprXact3D.cs`) rebuilds
    the native 3D structs for the XACT paths that stay — **delete it in 5c-3b**. Patcher set 197→203.
    Build-verified across WPR.Framework.Xna, FNA, WPR.Backend.FNA, WPR.XnaCompability, WPR.Loader, GamerServices.
  - *(original 5c-3a scope, for reference)* `SoundEffect`, `SoundEffectInstance`,
    `DynamicSoundEffectInstance`, `AudioListener`, `AudioEmitter`, `Microphone` + the pump
    inversion + a microphone seam (5 `FNAPlatform` calls). Verified independent: this set does
    not reference the XACT set or Media. (`SoundBank`/`Cue` reference *it*, which is fine —
    staying code may reference moved code.)
  - **5c-3b — XACT. ✅ LANDED + RUNTIME-VERIFIED 2026-08-08 (Version 8, reinstall-forcing).**
    `AudioEngine`/`SoundBank`/`WaveBank`/`Cue`/`AudioCategory` moved behind a ~40-member
    `IXactBackend`; the temporary `WprXact3D.cs` was deleted, leaving `fna/src/Audio/` empty.
    The callback-bearing parts stayed on the backend side of the seam by design: the
    `[MonoPInvokeCallback]` union-decoder and the `FACTNotificationDescription`/runtime-params
    delegate lifetimes live in `FnaXactBackend`, and `AudioEngine` sees only
    `OnXactNotification(XactNotificationKind, IntPtr)`. `SoundBank.dspSettings` (an
    `F3DAUDIO_DSP_SETTINGS` plus an `AllocHGlobal` coefficient buffer shared with `Cue`) became
    backend-owned, with `SoundBank.Build3DParams` preserving XACT's exact inputs
    (`SourceChannels = 1`, `CurveDistanceScaler = float.MaxValue`). `WaveBank`'s streaming path
    normalisation + `fopen` moved into `CreateStreamingWaveBank` (they needed FNA's
    `TitleLocation`/`FileHelpers`). Index sentinels `== 0xFFFF` → `< 0`; `AudioCategory.index`
    `ushort`→`int`. Patcher set 203→208. **User confirmed XACT audio works after reinstall.**
  - **5c-3c — Media. ✅ LANDED + RUNTIME-VERIFIED 2026-08-08 (Version 9, reinstall-forcing).** All six types
    (`MediaPlayer`/`Song`/`SongCollection`/`MediaQueue` + `Xiph/Video`+`VideoPlayer`) moved;
    `fna/src/Media/` is gone. **Seam = one `IMediaBackend`, not two.** The other seams split by
    native facility, and under FNA these two regions genuinely are different libraries (FAudio's
    `XNA_*` song player vs. Theorafile) — but they are one XNA subsystem with one lifetime, always
    registered and torn down together, and a non-FNA backend typically implements both with a
    single facility (Media Foundation plays both). XACT kept its own slot because it is genuinely
    optional and has its own engine handle. Shape follows 5c-3a: `tf_*`'s `int` booleans become
    `bool`, `th_pixel_fmt` becomes a WPR-owned `VideoPixelFormat`; the YUV plane buffer and the
    pinned audio float array stay raw `IntPtr`s because `VideoPlayer` owns them and re-hands the
    same pointer every frame. Two knots resolved:
    (a) **the media pump inverted**, exactly as 5c-3a did for `DynamicSoundEffectInstance` — the
    `ActiveSongChanged`/`MediaStateChanged` dirty flags moved off `FrameworkDispatcher` onto
    `MediaPlayer` (renamed `INTERNAL_*`, since `MediaPlayer` already has *public events* of those
    names) and the dispatcher now calls one `MediaPlayer.PumpUpdate()`. `FrameworkDispatcher` is
    now a pure ordering shell holding no state; it moves in 5c-5 with `TouchPanel`.
    (b) **`Song.FromUri` needed `TitleLocation`**, so `XnaBackend` gained a `SetTitleLocation`
    hook — deliberately a hook, because the answer depends on how the host launched the title
    (per-game install folder vs. host exe dir); 5c-4's content rooting will reuse it.
    ⚠ **`Microsoft.Xna.Framework.Media.SongCollection` was deliberately EXCLUDED from
    `WprFrameworkXnaTypes`** at the time. WPR.Framework.Xna does define it (it is a
    `MediaPlayer.Play` overload), but a pre-existing `Patches` entry redirected games'
    `SongCollection` to the WP7 `MediaLibrary` shim in `WPR.XnaCompability.Media` — and
    `WprFrameworkXnaTypes` is tested **first** in `PatchDll`, so listing it would have silently
    stolen that redirect. Patcher set 208→213 (5 of the 6 moved types). Build-verified across
    WPR.Framework.Xna, FNA (x64), WPR.Backend.FNA, WPR.XnaCompability, GamerServices, WPR.Loader.
    **Resolved in 5c-6** — the duplicate type is gone and the exclusion with it.
- **5c-4 — Content pipeline. ✅ LANDED + RUNTIME-VERIFIED 2026-08-08 (Version 10, reinstall-forcing).** The
  prediction held: this really was mostly a *move*. 63 `Content/**` files went across, and an audit
  of everything they reach into found only **six** FNA-side symbols at eleven sites —
  `TitleContainer.OpenStream`, `FileHelpers.{ResolveRelativePath,NormalizeFilePathSeparators}`,
  `TitleLocation.Path`, `FNA3D_Supports{DXT1,S3TC}`, `FNALoggerEXT.LogWarn`. Resolution:
  - **`TitleContainer` and `MonoGame.Utilities.FileHelpers` moved too** rather than being seamed.
    `FileHelpers` is pure string manipulation with no platform tie. `TitleContainer` is a *public
    game-facing XNA API* whose body is by now mostly WPR-authored (the `Content/Scenes/` fallback
    for FFWD-style titles, the localization-fallback rethrow, the open tracing) — that logic should
    not be stranded in the vendored tree. Its one real platform tie was the internal
    `ReadToPointer` → `FNAPlatform.ReadFileToPointer`, which had exactly one caller
    (`FnaXactBackend.ReadFileToPointer`), so its body **came down into that backend** and the
    method is gone. `TitleContainer` now needs nothing but the `XnaBackend.TitleLocation` hook.
  - `FNA3D_SupportsDXT1/S3TC` were **already on the RHI** from 5c-1 — a mechanical swap.
  - `XnaBackend` gained a `LogWarn` sink beside `LogInfo` (one call site: ContentManager's
    "asset loaded as a different type than requested" notice).
  - One genuine surprise: `Texture2DReader` used **FNA's internal
    `MemoryStream.TryGetBuffer(out byte[])` extension**, an old-Mono shim that stayed behind in
    FNA and doesn't match the BCL's `ArraySegment` overload. 5c-1 had already hit this and left
    `Graphics/ImageStreamHelper.TryGetBuffer` behind for the DDS loaders, so the reader now uses it.
  - `WPR.Framework.Xna` gained `[InternalsVisibleTo("WPR.Backend.FNA")]` (the backend shares the
    moved `FileHelpers` path helpers). Visibility only — it still references nothing.

  **Risk #1 checks, both clean.** The teardown ladder's reflective clear of
  `ContentTypeReaderManager.contentReadersCache` resolves via compile-time `typeof`, so it rebinds;
  the private-static field name is preserved verbatim. And `ContentTypeReaderManager.assemblyName`
  — which rewrites XNB reader type strings like `", Microsoft.Xna.Framework.Graphics"` onto the
  runtime assembly — is computed as `typeof(ContentTypeReaderManager).Assembly.FullName`, so it
  **self-corrects** to `WPR.Framework.Xna`, which is exactly where all the built-in readers now
  live. Game-supplied custom readers carry their own assembly name and are unaffected.

  Patcher set 209→216 (`ContentManager`, `ContentReader`, `ContentTypeReader`, the **open generic**
  `ContentTypeReader\`1` — games subclass it, and Cecil renders that FullName with the arity suffix —
  `ContentTypeReaderManager`, `ResourceContentManager`, `TitleContainer`). Verified against the built
  DLL: no stale entries, no `Patches` collisions, and the only public XNA type not in the set is the
  then-deliberately-excluded `Media.SongCollection` (removed as an exception in 5c-6).
  Build-verified across all six projects.
- **5c-5 — Input devices (+ Storage). ✅ LANDED 2026-08-08 (Version 11, reinstall-forcing).**
  All 11 `Input/**` files moved, plus two things the original decomposition had not assigned:
  - **`FrameworkDispatcher` finally moved.** It was the 5c-3a dependency knot — one `Update()`
    touching audio, media *and* input. Each of 5c-3a/5c-3c inverted one piece of its state onto the
    owning type; `TouchPanel` was the last holdout, so with input across it became pure ordering and
    came too. It now holds **no state at all**.
  - **`StorageDevice`/`StorageContainer`** — a genuine gap in the plan rather than scope creep: the
    locked 5c boundary is "lift the type system, leave the spine", and these are game-facing XNA
    types, not spine. Two FNAPlatform calls (`GetStorageRoot`, `GetDriveInfo`) behind a small
    `IStorageBackend`. It got its own seam rather than two `XnaBackend` hooks because a non-desktop
    backend answers "where do saves live" completely differently — naming it makes that an
    obligation instead of an inherited desktop assumption.

  **Seam location corrected again, same reason as 5c-0.** The plan said input would ride
  `WPR.Abstractions.Input.IInputProvider`; that hits the identical cycle — the input vocabulary *is*
  the XNA types (`GamePadState`, `Keys`, `TouchPanelCapabilities`, `GamePadDeadZone`), which live in
  `WPR.Framework.Xna`. So `IInputBackend` sits beside the other seams in `WPR.Xna.Rhi`, and
  `IInputProvider` stays what it always was: the generic host-level input abstraction, unrelated to
  the XNA device API. Unlike audio, the seam is a **1:1 mirror of FNA's `FNAPlatform` input delegate
  table** (18 members) — every one already takes and returns WPR-owned value types, carries no
  delegates or native structs, and is poll-grained, so there was nothing to reshape.

  **Only pull operations cross the seam.** Event *delivery* still flows from `SDL2_FNAPlatform`'s
  event loop straight into the moved types' internals (`Keyboard.keys`, `Mouse.INTERNAL_*`,
  `TouchPanel.INTERNAL_onTouchEvent`/`SetFinger`/`EnqueueGesture`, `TextInputEXT.OnTextInput`, …)
  via `InternalsVisibleTo("FNA")` — the push-into-moved-types arrangement the spine already used.
  That is why FNA compiled unchanged despite ~50 such write sites.

  One severance: `Mouse`'s four size fields seeded from `GraphicsDeviceManager.DefaultBackBuffer*`
  (spine). Inlined as 800/480 exactly as 5c-1 did in `PresentationParameters` — they are overwritten
  on the first resize and on every device reset. Also **deleted `FNAInternalExtensions.cs`**, now
  dead: its `MemoryStream.TryGetBuffer(out byte[])` old-Mono shim lost its last caller when 5c-4
  moved `Texture2DReader` onto `ImageStreamHelper`.

  Patcher set 216→229 (11 input types + `FrameworkDispatcher` + the 2 storage types); verified
  against the built DLL — no stale entries, no duplicates, no `Patches` collisions, and the only
  public XNA type not in the set is still the then-excluded `Media.SongCollection` (resolved in 5c-6).
  Build-verified across all six projects.
- **(later) spine stage** — `Game`/`GameWindow`/`GraphicsDeviceManager`/`FNAPlatform`, window
  ownership, present-into-shell, and promoting the `ApplicationLaunch` teardown ladder onto
  `FnaGameHost` behind real `TeardownPhase` hooks. Gated on the window-compositing product call.

After 5c-1…5c-5 the fitness baseline drops all `Microsoft.Xna.Framework.*` **except** the
spine set still defined in FNA.

## Where 5c ended up (2026-08-08)

**The type-system lift is complete.** FNA is down from ~50k LOC across six subsystems to **21
source files**, and every one of them is either spine or a native binding:

| Remaining in FNA | Files |
|---|---|
| Game loop + components | `Game`, `GameComponent`, `DrawableGameComponent`, `GameServiceContainer` |
| Window + device selection | `GameWindow`, `FNAWindow`, `GraphicsDeviceManager`, `GraphicsDeviceInformation`, `PreparingDeviceSettingsEventArgs` |
| Platform layer | `FNAPlatform`, `SDL2_FNAPlatform`, `TitleLocation`, `FNALoggerEXT` |
| Native bindings | `FNA3D.cs`, `FAudio.cs`, `SDL2.cs`, `Theorafile.cs` |
| Build/host glue | `FNADllMap`, `AssemblyHelper`, `XamarinHelper`, `AssemblyInfo`, `NamespaceDocs`, `WprActivationGuard`, `WprGameThread` |

**Seven seams** now sit in `WPR.Xna.Rhi`, all registered in `FnaGameHost.RunAsync` and all
implemented by `WPR.Backend.FNA`: `IGraphicsBackend`, `IAudioBackend`, `IXactBackend`,
`IMediaBackend`, `IInputBackend`, `IStorageBackend`, plus the `XnaBackend` hook set
(`TitleLocation`, `LogInfo`, `LogWarn`, backbuffer-size).

**Two shape rules held up across all five sub-stages** and are the transferable lesson:
1. **Mirror the C ABI when it is already written in owned value types** (graphics, input) —
   the rewrite is mechanical and the contract validates itself by compiling the adapter.
   **Sit above the ABI when it carries delegates or native structs** (audio, XACT, media) —
   otherwise you hand-marshal GC-lifetime hazards across the seam.
2. **Only pull operations belong on a seam.** Push (event delivery, state the platform writes)
   keeps flowing straight into the moved types' internals via `InternalsVisibleTo("FNA")`. This is
   why the spine kept compiling unchanged at every step despite writing into moved statics
   constantly, and it is what makes the remaining spine stage tractable.

### Tree relocation + cleanup (post-5c-5)

With the lift done, the FNA fork moved from `Src/ThirdParty/fna` to
**`Src/Backends/FNA.Platform`**, beside `WPR.Backend.FNA` — its only consumer. The old name had
stopped being true: what remains is either WPR-authored (`WprGameThread`, `WprActivationGuard`),
heavily WPR-modified (`Game`, `SDL2_FNAPlatform`), or a native binding. `Src/ThirdParty` still
holds the genuinely-vendored deps (Icons.Avalonia, assembly-store-reader). In `Src/WPR.sln` the
project also moved out of the "Third Party" solution folder into "Backends".

**The assembly is still named `FNA` and must stay that way** — `InternalsVisibleTo("FNA")`,
`FNADllMap`'s declaring-assembly-keyed DllImport resolver, and `ApplicationPatcher.FNARef` all
depend on the name. See the new `Src/Backends/FNA.Platform/README.md`.

Also removed, all provably unreferenced: upstream's `FNA.csproj`/`_FNA.csproj`/`FNA.sln`/
`_FNA.sln`/`Makefile` (stale .NET Framework projects still listing the pre-migration file set)
and the `abi/` XNA-ABI facade set — WPR does that job via `ApplicationPatcher`, and its own
equivalent (`Src/Core/XnaFacades`) went in 5c-0b. Dropped `.gitmodules`, which described four
submodules that are actually vendored as plain files. `lib/` is otherwise untouched: only 3 of
its 487 files compile (the C# bindings), but the native sources are kept deliberately so a
native can be rebuilt. The stock-effect and YUV **HLSL sources** followed their compiled `.fxb`
blobs into `WPR.Framework.Xna/Graphics/Effect/`, where 5c-1 had left them orphaned.

### 5c-6 — the WP7 MediaLibrary types (Version 12, reinstall-forcing)

A follow-up audit of `WPR.XnaCompabilityPatch` asked which of its types were misfiled. Answer:
the whole `Media/` folder — **11 real WP7 XNA types** (`MediaLibrary`, `Album`, `AlbumCollection`,
`Artist`, `ArtistCollection`, `Genre`, `Picture`, `PictureCollection`, `SongCollection`,
`MediaSource`, `MediaSourceType`) that desktop FNA never implemented, so WPR had stubbed them
under a fake `WPR.XnaCompability.Media` namespace and redirected games there. They now live in
`WPR.Framework.Xna/Media/` under their genuine names, joining the half of the namespace 5c-3c
already owned.

**This fixed a live bug.** Two `SongCollection` types existed. `MediaPlayer.Play(SongCollection)`
is declared in `WPR.Framework.Xna` against the *real* one, but `PatchDll` rewrites every
`Media.SongCollection` typeref unconditionally — including the one inside that methodref's
signature. So a game doing `MediaPlayer.Play(library.Songs)` emitted a call to an overload that
did not exist → `MissingMethodException`. Unifying the two removes the failure and the
`WprFrameworkXnaTypes` exclusion that existed only because of it. `SongCollection` gained an
`internal` parameterless ctor (FNA's only ctor wraps an existing `List<Song>`; the MediaLibrary
types need an empty one).

Dropped 11 `Patches` entries; rescope set 229→240. **The set is now an exact 1:1 with the public
XNA surface of `WPR.Framework.Xna` — 240 = 240, no exclusions, no stale entries, no collisions.**

**What correctly stays in `WPR.XnaCompabilityPatch`** (it is a *patch-target* assembly, not a type
system): `GraphicsDeviceManager2` derives from FNA's `GraphicsDeviceManager`, which is spine — moving
it would need `WPR.Framework.Xna → FNA`, a cycle; it goes when the spine goes. `GraphicsDevice2` and
`GraphicsAdapter2` are `MemberPatches` targets (single-method redirects forcing a 480×800
`DisplayMode`); being types distinct from the real ones is the whole point.

Also fixed in passing: `Artist.Albums` was `=> Albums`, self-referential — any game reading it hit an
uncatchable `StackOverflowException`. Now `=> _Albums`.

**With that, "games bind only WPR-owned identities" holds for the entire XNA type system.** The only
remaining game-facing types they do not own are the spine set above — chiefly `Game`, `GameWindow`
and `GraphicsDeviceManager` — plus the deliberate `GraphicsDeviceManager2`/`GraphicsDevice2`/
`GraphicsAdapter2` behaviour-override shims.

## Risk ranking (carried from the sizing audit, re-pointed at 5c)

1. **Teardown-ordering + the backend registry (highest).** The `ApplicationLaunch` finally-
   ladder disposes audio-before-engine, `Game.Dispose` before ALC-unload, and reflectively
   clears `ContentTypeReaderManager.contentReadersCache` + sibling static registries in a
   strict order (fixes for ALC-unload-fail / stuck-audio / duplicate-static-key). Moving the
   types changes *where those statics live* and adds a **new** static (the backend registry)
   that holds native-adjacent state — it MUST be cleared at the right point or it pins the ALC
   / leaks the device. Every reinstall-forcing step needs a launch→exit→relaunch cycle, not
   just a launch.
2. **XNA render-path correctness.** Games bind by identity; a `SpriteBatch`/`Effect`/state bug
   renders wrong *everywhere*. Mitigation: 5c-1/5c-2 are mechanical leaf-swaps (logic
   unchanged), smoke pair (Minesweeper + MonstaFish) each step.
3. **Perf.** Non-issue if the RHI stays draw-call-grained (not per-vertex). Called out so the
   seam isn't accidentally chatty.
4. **The spine / window compositing.** Deliberately deferred; flagged because it's the one
   piece that is genuinely *reimplementation you can't stage as a leaf-swap*, and it's coupled
   to a product decision.

## Decisions (locked 2026-08-07)

- **Scope boundary: type-system only.** 5c lifts Graphics/Audio/Media/Content/Input behind the
  seams; the spine (`Game`/`GameWindow`/`GraphicsDeviceManager`/`FNAPlatform` + window ownership
  + teardown-ladder promotion) stays in `WPR.Backend.FNA` and becomes a distinct later stage,
  gated on the window-compositing product call.
- **Seam shape: FNA3D-mirroring handle RHI** (owned value types + opaque `IntPtr` handles), per
  the recommendation above. Audio/video follow the same C-API-mirroring pattern.
- **Phasing:** 5c-0 (seams + injection, non-reinstall) → 5c-1…5c-5 (each a reinstall-forcing
  leaf-swap that ends green + smoke-tested).
