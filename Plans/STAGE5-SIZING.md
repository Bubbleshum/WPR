# Stage 5 sizing audit — what is still unsevered

**Original audit:** 2026-08-05 · **Trimmed to outstanding work 2026-08-30.**
The full per-project sizing (nine clusters, all but two now closed) is in
`git show 2ce1cd2c:Plans/STAGE5-SIZING.md`.

This file survives for two reasons: the **residual clusters** below, and the **risk
register**, whose Risk #1 is cited by name from
`WPR.Abstractions/Hosting/IGameHost.cs` and `WPR.Abstractions/Audio/IAudioDevice.cs`.
Risk numbering is therefore frozen — do not renumber.

## Bottom line

The XL part of Stage 5 — reimplementing the XNA type system in `WPR.Framework.Xna`
and standing up `WPR.Backend.FNA` — is done. What is left is the **spine** and the
two clusters that hang off it.

## Residual clusters

| Cluster | Target | Effort | Why it is still open |
|---|---|---|---|
| Runtime host | promote `ApplicationLaunch` onto `FnaGameHost`; split ALC/lifecycle back into `WPR.Runtime` | **M** | The file *moved* into the backend verbatim in Stage 4, but it is still a `public static class` and `FnaGameHost` is a shim over it — `Shutdown()` == `RequestExit()`, `Activated`/`Deactivated` never raised. The teardown ladder still runs in `ApplicationLaunch`'s `finally`. Fragile: see Risk #1. |
| Tilt components | `TiltInputXnaComponent` / `TiltOverlayXnaComponent` → `WPR.Backend.FNA` | **DONE** 2026-09-01 | Was listed here as "S, spine-blocked", on the reasoning that a `GameComponent` has to derive from *something* so the destination type only settles with the spine. That was wrong: deriving from a backend type is only a leak *outside* an allowed referrer, so relocating into `WPR.Backend.FNA/Input/` was always sufficient. The head keeps the policy half behind `WPR.Xna.Rhi.ITiltEmulationHost` — it cannot move down, because it shares a binding table with the Silverlight host and so speaks `Avalonia.Input.Key`. This emptied `KnownBackendLeaks`. |
| The spine | see `ARCHITECTURE-MIGRATION.md` §5 Stage 5f | **L→XL**, product-gated | `Game` / `GameWindow` / `GraphicsDeviceManager` / `FNAPlatform` + window ownership. Blocked on the keep-the-SDL-window-or-composite-into-the-shell decision, not on effort. |

## R1 — "Severing FNA" is mostly *relocation into the backend*, not *abstraction*

This held across every cluster that has closed, and it still governs the two above.
Code that is *inherently* backend code should not be routed through an interface — it
should move. `TiltInput/OverlayXnaComponent` are FNA `GameComponent`s; `ApplicationLaunch`
owns and runs the FNA `Game` loop. Relocate them. Only the reimplemented XNA subsystems
genuinely consume a seam.

*(R2 — the abstraction-set gaps the audit exposed — is closed: `IGameHost` exists, and
the owned XNA value types landed in 5a. Note that `IGameHost` is the **only**
`WPR.Abstractions` interface with a consumer; see `ARCHITECTURE-MIGRATION.md` §5, "Stage 4
remnant".)*

## Risk ranking

**Numbering frozen — Risk #1 is cited from source.**

1. **Teardown-ordering regressions.** `ApplicationLaunch.cs` reaches FNA internals by
   reflection (`MediaPlayer.DisposeIfNecessary`, `SoundEffect.FAudioContext`,
   `ContentTypeReaderManager` cache clear) in a strict order that exists to fix
   ALC-unload, audio-leak, and duplicate-static-key bugs. `IGameHost.TeardownPhase`
   encodes that order; an implementation that ignores it silently regresses those
   hard-won fixes. The `XnaBackend` registry added in 5c compounds it: it holds
   native-adjacent state and must be cleared at the right point or it pins the ALC.
   Verify with **launch → exit → relaunch**, never a single launch.
2. **XNA render-path correctness.** Games bind these types by identity, so a subtle
   math, `SpriteBatch` or `Effect` bug shows up as wrong rendering *everywhere*.
   Mitigation: keep changes to mechanical leaf-swaps and run the smoke pair each step.
   *(2b — duplicate type forwarding across facades — is closed; the facade projects were
   deleted in 5c-0b and each moved type has exactly one owning assembly.)*
3. **Silverlight present bridge.** `IBackgroundRenderer` passes Avalonia types
   (`DrawingContext` / `WriteableBitmap`) *through the seam*, deliberately — hoisting it
   into `WPR.Abstractions` would force an `Abstractions → Avalonia` edge. That is fine
   today, but a genuinely Avalonia-free backend means re-slicing the CPU-readback/present
   into a Platform layer across 3 TFMs. It lands in Stage 7, not before.
