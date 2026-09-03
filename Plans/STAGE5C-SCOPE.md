# The XNA↔backend seam — design of record, and the spine that is left

**Original scope doc:** 2026-08-07 · **Trimmed to outstanding work 2026-08-30.**
Sub-stages 5c-0 through 5c-6 have all landed; their narratives (and the
sub-stage-by-sub-stage record of what moved when) are in
`git show 2ce1cd2c:Plans/STAGE5C-SCOPE.md`.

This file is **not** a to-do list for 5c — that work is done. It survives as the
**design of record for the seven `WPR.Xna.Rhi` seams**, which is cited by name from
fourteen source files (`IGraphicsBackend`, `IAudioBackend`, `IXactBackend`,
`IMediaBackend`, `IInputBackend`, `IStorageBackend`, `XnaBackend`, and their `Fna*`
implementations), and as the place the **spine stage's** design constraints are written
down. The spine stage itself is defined in
[`ARCHITECTURE-MIGRATION.md`](ARCHITECTURE-MIGRATION.md) §5, Stage 5f.

---

## Design of record: why the graphics seam is FNA3D-shaped

**`FNA3D` is already a clean, low-level RHI, and its C structs are already written in the
value types WPR owns.** `FNA3D_BlendState` / `SamplerState` / `RasterizerState` /
`DepthStencilState` / `PresentationParameters` are composed of `Blend`, `BlendFunction`,
`SurfaceFormat`, `TextureFilter`, `TextureAddressMode`, `Color`, `DepthFormat`,
`PresentInterval` — all WPR-owned — plus `IntPtr` opaque handles for
device/texture/buffer/effect/rendertarget.

So the seam **mirrors the FNA3D C API almost 1:1**, expressed in WPR-owned value types and
opaque handles. Not an object-oriented "resource" abstraction. Rationale:

- The vocabulary already exists — signatures need no new types.
- FNA's `GraphicsDevice`/`Texture2D`/`Effect`/buffers are thin managed wrappers over
  exactly these calls, so lifting them was mechanical: replace each `FNA3D_Foo(...)` leaf
  with `_backend.Foo(...)`, preserving ~90% of the managed logic verbatim (state caching in
  `PipelineCache`, `SpriteBatch` batching, `EffectParameter` marshalling, DXT decode).
- A future `WPR.Backend.Direct3D11` implements the **same** interface over Vortice — a
  C-style handle RHI maps cleanly onto D3D11, and forces the abstraction not to be
  accidentally FNA-internal-shaped.
- Draw calls are coarse-grained (one per `SpriteBatch` flush / primitive batch, not per
  sprite or vertex), so interface dispatch is free next to the P/Invoke + GPU cost.

**Seam location.** The RHI lives in **`WPR.Framework.Xna`** (namespace `WPR.Xna.Rhi`), not
`WPR.Abstractions`. The RHI vocabulary *is* the XNA enums, which live in
`WPR.Framework.Xna`; putting the RHI in the dependency-free `WPR.Abstractions` would force
`Abstractions → WPR.Framework.Xna`, and since `WPR.Framework.Xna` consumes the RHI that is
a **cycle**. Any backend rendering XNA content already references the XNA type system, so
co-locating the contract with it is not a layering violation — and it keeps
`WPR.Abstractions` genuinely generic (host/window/lifecycle only). The same reasoning put
`ISurfaceRendererBackend` in `WPR.Framework.Silverlight` (Avalonia vocabulary) and
`IAchievementStore` in `WPR.Framework.Xna` (the game-facing `Achievement` type).

## Design of record: the directional inversion and how impls are injected

`WPR.Framework.Xna` *defines* `GraphicsDevice` and FNA *implements the RHI it calls*:

- `WPR.Framework.Xna` owns the seams and its `GraphicsDevice`/`SpriteBatch`/… call
  `IGraphicsBackend`. It references almost nothing — it stays a dependency-light,
  fitness-clean leaf.
- `WPR.Backend.FNA` holds `FnaGraphicsBackend : IGraphicsBackend` and its siblings, which
  P/Invoke `FNA3D`/`SDL2`. **Superseded 2026-09-01 for the audio three:** the FAudio, FACT
  and `XNA_Song`+Theorafile adapters moved to `Src/Modules/Audio/WPR.Audio.FAudio` and are composed
  through `AudioBackendRegistry` rather than registered directly — see
  ARCHITECTURE-MIGRATION §5, "Audio split". The seam design below is unchanged; only the
  host assembly moved.
- **Injection** inverts FNA's own `FNAPlatform` static-delegate-table pattern: at
  `FnaGameHost.RunAsync` startup the backend registers its impls into the `XnaBackend`
  registry **before** the game's `GraphicsDevice` is constructed, and **clears them on
  teardown** (Risk #1 — a backend registry must not outlive the run, or it pins the ALC).
  The window handle flows in as an opaque `IntPtr`: the spine sets
  `PresentationParameters.DeviceWindowHandle`, and WPR's `GraphicsDevice` passes it
  straight to `IGraphicsBackend.CreateDevice`. That opacity is what makes the split viable.

Seven seams are filled in `FnaGameHost.RunAsync` today: `IGraphicsBackend`, `IPlatformBackend`,
`IInputBackend` and `IStorageBackend` registered directly, the audio three
(`IAudioBackend`, `IXactBackend`, `IMediaBackend`) composed by `AudioBackendRegistry` from
the registered `IAudioModule`s, plus the `XnaBackend` hook set (`TitleLocation`, `LogInfo`,
`LogWarn`, back-buffer size, game-thread post, focus-activation suppression).

## The two shape rules — apply these to every new seam

These held across all five 5c sub-stages and are the transferable lesson:

1. **Mirror the C ABI when it is already written in owned value types** (graphics, input) —
   the rewrite is mechanical and the contract validates itself by compiling the adapter.
   **Sit above the ABI when it carries delegates or native structs** (audio, XACT, media) —
   otherwise you hand-marshal GC-lifetime hazards across the seam. Where the vocabulary is
   a type WPR does *not* own (Vortice, Avalonia), the seam sits above the ABI by the same
   logic — that is why `ISurfaceRendererBackend` is two factory methods, not a D3D11 mirror.
2. **Only pull operations belong on a seam.** Push — event delivery, state the platform
   writes — keeps flowing straight into the moved types' internals via
   `InternalsVisibleTo("FNA")`. This is why the spine kept compiling unchanged at every
   step despite writing into moved statics constantly, **and it is what makes the remaining
   spine stage tractable.**

---

## What is left: the spine

The stage definition, the file inventory and the exit criteria are in
[`ARCHITECTURE-MIGRATION.md`](ARCHITECTURE-MIGRATION.md) §5, Stage 5f. What belongs here is
why it is a different *kind* of work from the resource lift, because that shapes how it
must be scoped:

1. **FNA renders into its own top-level SDL window, and there is no Avalonia bridge for the
   game path.** Confirmed by audit: no `D3D11Image`, no shared texture, no `SetParent`
   anywhere near it; Avalonia is only the launcher shell. `SDL2_FNAPlatform` alone carries
   ~741 SDL calls covering window creation, the event pump and the main loop. Moving window
   ownership into WPR means *building* the bridge — which is a **product** decision (keep
   the separate game window, or composite into the shell?), not a refactor. Scope the code
   after that call, not before.
2. **The teardown ladder lives in the spine.** `ApplicationLaunch`'s `finally` is where the
   ordered dispose happens, so the spine stage and the Stage-4 host promotion are one piece
   of work reached from two directions. Sequence them together.
3. **Rule 2 above is the reason it is tractable at all.** Because the spine only ever
   *pushes* into the moved types, every seam it needs is a pull the other way — the same
   shape as the six that already exist.

**Standing trade-off while it is unstarted:** `Microsoft.Xna.Framework.Game` remains a
*backend-defined* game-facing identity (the patcher rescopes game `Game` refs to FNA). That
is defensible — `Game` is inseparable from the platform loop — but it means "games bind
only WPR-owned identities" is true of the entire XNA type system and false for the spine
set.

## Live risks

1. **Teardown-ordering + the backend registry (highest).** The `ApplicationLaunch` finally-
   ladder disposes audio-before-engine and `Game.Dispose` before ALC-unload, and reflectively
   clears `ContentTypeReaderManager.contentReadersCache` and sibling static registries in a
   strict order (fixes for ALC-unload-fail / stuck-audio / duplicate-static-key). The
   `XnaBackend` registry holds native-adjacent state and MUST be cleared at the right point
   or it pins the ALC / leaks the device. Every reinstall-forcing step needs a
   **launch → exit → relaunch** cycle, not just a launch.
2. **XNA render-path correctness.** Games bind by identity; a `SpriteBatch`/`Effect`/state
   bug renders wrong *everywhere*. Mitigation: mechanical leaf-swaps, smoke pair each step.
3. **Perf.** Non-issue while the RHI stays draw-call-grained. Called out so no future seam
   is accidentally made chatty.
4. **The spine / window compositing.** The one piece that is genuinely reimplementation you
   cannot stage as a leaf-swap, and it is coupled to a product decision.

## Locked decisions

- **Scope boundary: type-system only.** 5c lifted Graphics/Audio/Media/Content/Input behind
  the seams; the spine (`Game`/`GameWindow`/`GraphicsDeviceManager`/`FNAPlatform` + window
  ownership + teardown-ladder promotion) stays in `WPR.Backend.FNA` until its own stage,
  gated on the window-compositing product call.
- **Seam shape: FNA3D-mirroring handle RHI** (owned value types + opaque `IntPtr` handles).
  Audio/video follow the C-API-mirroring pattern only where rule 1 says they should.
- **Every step that moves a type out of FNA follows the no-redirect contract** —
  `ApplicationPatcher.WprFrameworkXnaTypes` + `module.AssemblyReferences.Add` + `Version`
  bump + reinstall all games + smoke-test. See `ARCHITECTURE-MIGRATION.md` §3.
