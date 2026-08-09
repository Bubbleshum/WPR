# Stage 5 sizing audit — FNA/Vortice severance

**Date:** 2026-08-05 · **Method:** four parallel read-only audits of every leaking
project (baseline from `BackendIsolationTests`). This is the effort behind the
migration's milestone stage (§5 of `ARCHITECTURE-MIGRATION.md`): removing the
`FNA` and `Vortice.*` edges from Runtime + Frameworks.

## Bottom line

**Stage 5 is XL — plausibly as large as all other stages combined** — and the
cost is overwhelmingly concentrated in **one net-new project (`WPR.Framework.Xna`)
reimplementing the XNA type system**, plus standing up **`WPR.Backend.FNA`**. The
rest (Silverlight, Sensors, GamerServices, UI) is real but secondary and mostly
*unblocks once two things exist*: the **owned XNA math types** and the
**`WPR.Backend.FNA` adapter**.

## Effort by cluster

| Cluster | Target | Effort | Why |
|---|---|---|---|
| XNA type system | new `WPR.Framework.Xna` | **XL** | 8 facades are *pure* `TypeForwardedTo` shims — games bind to FNA identities. Severing = ground-up reimpl of XNA math + Graphics + Game + Content over abstractions/owned types. Concentrated in base-math + Graphics (XL), Game/Content (L). |
| Runtime host | `ApplicationLaunch.cs` → `WPR.Backend.FNA` (`IGameHost`) | **L→XL** | Coupling is one file (the game-loop host); rest of Runtime is FNA-free. Fragile: reflective teardown ordering. |
| Silverlight renderer | `WPR.Backend.Direct3D11` | **M** | ~530 LOC, 3 Windows-only files behind a clean `IBackgroundRenderer` seam; no swapchain. |
| `WPR.XnaCompability` | relocate into `WPR.Backend.FNA` | **M–L** | *Subclasses* concrete FNA classes (`: GraphicsDevice`, `: GraphicsDeviceManager`) — not abstractable; must move. Patcher redirect target → table edit + reinstall. |
| `WPR.UI` / `WPR.UI.Android` | relocate + orientation abstraction | **M** | Genuine leaks = `TiltInput/OverlayXnaComponent` (`GameComponent`s) + `ResolveXnaKey(Keys)` → move to backend; launchers keep backend access via an orientation abstraction. |
| `Microsoft.Devices.Sensors` | owned `Vector3` | **S** | One FNA type, one public struct field (`AccelerometerReading.Acceleration`). Blocked only on owned math. |
| GamerServices — framework surface | owned types / `ITexture` | **M** | FNA in public signatures (avatar `Matrix[]`/`Vector3`, `GamerPicture` `Texture2D`, `PlayerIndex`, `StorageDevice`). Avatar API is mostly `NotImplementedException` stubs — cheap to convert. |
| GamerServices — scraper + SQLite DB | Runtime `IAchievementStore` | **S** | **Confirmed FNA-free.** Clean lift out of the framework into Runtime. |

## Two structural refinements to the plan

### R1 — "Severing FNA" is mostly *relocation into the backend*, not *abstraction*

A large fraction of the coupling is code that is *inherently* backend code and
cannot (and should not) be routed through an interface:

- `WPR.XnaCompability` subclasses FNA graphics classes.
- `ApplicationLaunch.cs` owns and runs the FNA `Game` loop.
- `TiltInput/OverlayXnaComponent` are FNA `GameComponent`s.
- `WprGameThread` / `WprActivationGuard` are WPR code that merely *lives* in the
  vendored FNA source tree today.

The clean move for all of these is **relocate into `WPR.Backend.FNA`**. Only the
reimplemented XNA `Graphics`/`Game`/`Content`/`Audio`/`Input`/`Media` in
`WPR.Framework.Xna` genuinely consume the `WPR.Abstractions` interfaces.

### R2 — The abstraction set has two gaps the audit exposed

The Stage-1 interface list is necessary but **not sufficient**. Add:

1. **`IGameHost`** — a game-loop/lifecycle contract. `Game`/`GameComponent`/
   `GraphicsDeviceManager` and `ApplicationLaunch`'s host driver need it, and it
   must expose **explicit teardown-ordering hooks** (see Risk #1).
2. **An "owned value types" workstream, separate from the interfaces.** The XNA
   **math types cannot be interfaces** — `Vector2/3/4`, `Matrix`, `Quaternion`,
   `Color`, `Rectangle`, `Point`, `Plane`, `Ray`, `BoundingBox/Sphere/Frustum`,
   plus `PlayerIndex`, `DisplayOrientation`, `StorageDevice` — they are value
   types/enums games construct and pass **by value/identity**. They must become
   **WPR-owned concrete types under the `Microsoft.Xna.Framework` namespace**,
   realistically by **vendoring FNA's MIT-licensed math sources** under our
   namespaces. This is the **critical-path prerequisite**: it blocks the facades,
   Sensors, and the GamerServices avatar surface simultaneously.

## Recommended Stage 5 decomposition (each sub-stage ends green)

- **5a — Owned XNA value/math types.** Vendor FNA's MIT math + owned
  `PlayerIndex`/`DisplayOrientation`/`StorageDevice`. Prereq for 5c/5d. No FNA
  removed yet; adds `WPR.Framework.Xna`'s type foundation.
- **5b — Stand up `WPR.Backend.FNA`.** Adapter implementations of the abstractions
  + **relocate the inherently-backend code** (R1): `WPR.XnaCompability` subclasses,
  the `ApplicationLaunch` host driver behind `IGameHost`, `WprGameThread`/
  `WprActivationGuard`, the Tilt components. Fitness baseline drops
  `WPR.XnaCompability`, `WPR.UI`, `WPR.UI.Android`, and `WPR` core.
- **5c — Reimplement XNA `Graphics`/`Game`/`Content`/`Audio`/`Input`/`Media`** in
  `WPR.Framework.Xna` over abstractions + 5a types; **re-point the 8 facade shims
  from FNA → `WPR.Framework.Xna`** (assign each forwarded type exactly one owning
  assembly — see Risk #2b). Fitness baseline drops all `Microsoft.Xna.Framework.*`.
- **5d — De-FNA the peripheral framework surfaces.** `Microsoft.Devices.Sensors`
  → owned `Vector3`; GamerServices avatar/profile/`Guide` → owned types/`ITexture`.
  Baseline drops `Microsoft.Devices.Sensors`, `Microsoft.Xna.Framework.GamerServices`.
- **5e — GamerServices scraper/DB → Runtime `IAchievementStore`.** Independent and
  FNA-free; **can land any time** (good early-momentum / parallel work).

`WPR.Backend.Direct3D11` (the Silverlight M) is independent of the XNA chain and
can also proceed in parallel.

## Risk ranking

1. **Teardown-ordering regressions.** `ApplicationLaunch.cs` reaches FNA internals
   by reflection (`MediaPlayer.DisposeIfNecessary`, `SoundEffect.FAudioContext`,
   `ContentTypeReaderManager` cache clear) in a strict order that exists to fix
   ALC-unload, audio-leak, and duplicate-static-key bugs. The abstractions have no
   equivalent hook — `IGameHost` must reproduce the exact ordering or silently
   regress those hard-won fixes.
2. **XNA type-system correctness.** Games bind to these by identity; a subtle math
   or `SpriteBatch`/`Effect` bug shows up as wrong rendering *everywhere*.
   - **2b — duplicate forwarding.** The same FNA type is forwarded from multiple
     facades (e.g. `SoundEffect` from base + Audio); on severance each type needs
     exactly one owning assembly or you get duplicate-definition conflicts.
3. **Silverlight present bridge.** `IBackgroundRenderer` passes Avalonia types
   (`DrawingContext`/`WriteableBitmap`) *through the seam*; an Avalonia-free backend
   means re-slicing the CPU-readback/present into a Platform layer across 3 TFMs.
4. **`WPR.XnaCompability` relocation** forces a patcher-table change + reinstall
   (well-understood, but a coordination cost, and it's a game-IL redirect target).

## Note on the achievements split

The GamerServices scraper + SQLite DB (`Scraper.cs`, `GameToKey.cs`,
`AchievementContext.cs`, `Achievement.cs`, `AchievementCollection.cs`) is confirmed
**FNA-free**, so decision #3 (achievements → Runtime `IAchievementStore`) is a clean
lift with no backend entanglement. The UI's GamerServices references
(`*ViewModel`, `SettingsPage`) already use only that FNA-free surface and repoint
to `IAchievementStore` for free.
