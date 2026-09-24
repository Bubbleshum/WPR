# Collapsing the Silverlight host into the XNA host

**Status:** Plan. Nothing here is built. Stage A is work that is already owed for two titles
regardless of whether the rest is ever done.

**Verdict:** The two hosts should become one, and the reason is not tidiness — **the line WPR
currently draws between them is in the wrong place.** Two titles routed through the *XNA* host
need more Silverlight rendering than the titles routed through the *Silverlight* host. The split
is on `ApplicationType` + `MixedModeDetection`, and the thing it claims to separate is a
continuous gradient with no gap in it.

**Prize:** 97 pure-Silverlight titles in the 307-XAP library cannot run on Android at all today.
They are the largest single block of the library WPR cannot host on a phone.

---

## 1. The measurement that motivates this

Silverlight types referenced by **each game's own main assembly** (not the toolkit DLLs it
bundles, which inflate the count several-fold). "hard" counts the features neither
`SoftwareVisualRasteriser` nor the Silverlight framework implements today — text, transforms,
templates, virtualising lists, geometry.

| title | routed as | SL types | hard | the hard ones |
|---|---|---:|---:|---|
| Little Acorns | mixed -> XNA host | 16 | 0 | — |
| Cut the Rope | mixed -> XNA host | 21 | 1 | TextBlock |
| Sid Meier's Pirates! | mixed -> XNA host | 32 | 3 | TextBlock, ListBox, ItemsControl |
| Rabbids Go Phone | mixed -> XNA host | 38 | 1 | TextBlock |
| **Flowerz** | **Silverlight host** | **53** | **5** | + VisualStateManager, ScrollViewer, ScaleTransform, Path |
| **Minesweeper** | **Silverlight host** | **87** | **8** | + PlaneProjection, Popup |
| **Galactic Reign** | **mixed -> XNA host** | **117** | **11** | + RotateTransform, Geometry, Binding |
| **Carcassonne** | **mixed -> XNA host** | **139** | **16** | + TextBox, TransformGroup, Ellipse |

Carcassonne and Galactic Reign are hosted by `MixedModeGame` and are **more Silverlight-dependent
than Flowerz**, which is hosted by Avalonia. There is no threshold between the two groups.

This also falsifies the doc comment on `MixedModeGame`
(`Src/Engine/WPR.Engine.GameLoop/MixedModeGame.cs`, the "Nothing Silverlight is ever rendered,
and that is not a limitation being worked around — it is what these titles are" paragraph). That
is true of the four titles it was written against and false for at least two of the twelve. It
should be corrected whether or not this plan proceeds.

### Library-wide scale

Measured across all 307 XAPs by `RuntimeType` plus an assembly-reference test for
`Microsoft.Xna.Framework.Interop` (the interop DLL is a phone framework assembly, so it is a
*reference*, never a file in the XAP — a file test reports zero):

| | count |
|---|---:|
| `RuntimeType="XNA"` | 178 |
| `RuntimeType="Silverlight"`, **pure** | **97** |
| `RuntimeType="Silverlight"`, mixed mode | 14 XAPs / **12 distinct titles** |
| no manifest / WP8 native / other | 16 |
| unreadable | 2 |

The mixed set is larger than the ten worked on so far — it also contains **Connect 4** and
**Real Racing 2**, neither yet tried. (Pirates and The Game of Life each appear twice at
different versions.)

---

## 2. What is already built

Most of the bridge exists, because the mixed-mode work built it. All of this runs with **no
Avalonia**, on both heads:

| capability | where |
|---|---|
| Boot a Silverlight app in a per-game ALC | `WPR.Runtime.SilverlightAppHost.BootInContext` — **already shared by both launchers** |
| Host it as an ordinary `Game` | `WPR.Engine.GameLoop.MixedModeGame` |
| Rasterise a live visual tree to a `Texture2D` | `SoftwareVisualRasteriser` + `UIElementRenderer` — **Pirates' entire main menu is drawn this way today** |
| Run storyboards | `AnimationClock` |
| Measure/arrange the tree | `FrameworkElement`, re-run per navigation off `Frame.Navigated` |
| Hardware Back | `PhoneApplicationFrame.HandleBackKey()`, same route on both heads |
| Teardown of Silverlight statics | `ApplicationLaunch.ResetWprSingletons` |

Two further facts that make the remaining work smaller than it looks:

- **Only 22 of 253 files** in `WPR.Framework.Silverlight` mention Avalonia, and most of those
  only alias a type. `TextBlock.Measure` already carries a non-Avalonia fallback path.
- **The gesture recogniser is already framework-neutral.** `Gestures.cs`/`PointerInteraction`,
  `HitTester` and `PanoramaStateTable` name no Avalonia type. Only the plumbing in
  `PhoneApplicationFrameView.OnPointer*` does, and there it is `e.GetPosition(this)` and
  `InvalidateVisual()` — two lines of coupling around ~90 lines of portable logic.

---

## 3. What is missing

| # | gap | size | needed by |
|---|---|---|---|
| 1 | **Text rendering** | **the one hard dependency** | 7 of the 8 titles above |
| 2 | `RenderTransform`, `Projection`, clip, opacity layers | moderate | Minesweeper, Carcassonne, Galactic Reign, Flowerz |
| 3 | Gradients, `Path`/`Ellipse` geometry, `Popup` | moderate | Flowerz, Minesweeper, Carcassonne |
| 4 | WP control chrome the Avalonia renderer already draws — Button, ProgressBar, ToggleSwitch, Panorama, PanoramaItem | mechanical port | Silverlight titles generally |
| 5 | Input: `TouchPanel` -> Silverlight routed events + `GestureListener` | mechanical | all |
| 6 | `ControlTemplate` / `DataTemplate` / `VisualStateManager` / `Binding` / virtualising `ListBox` | large | Minesweeper, Carcassonne, Galactic Reign |

**Gap 6 is not a cost of this plan.** Those are framework features, not renderer features — they
are needed by Carcassonne and Galactic Reign *on the host they already use*, whichever code draws
the result. Listed only so the total is honest.

### On gap 1 — text

Nothing in the tree can rasterise a glyph. No SDL_ttf, no bundled font; the only TrueType code
present is an uncompiled `imstb_truetype.h` inside FAudio's `utils/`, which is not built and not
exported. Three options, in ascending cost:

1. **Pre-baked `SpriteFont` atlases for the WP7 theme sizes.** WP7 titles overwhelmingly style
   text with `PhoneTextNormalStyle` and its siblings — one family at a handful of sizes, already
   mirrored in `PhoneTheme.cs`. Covers most real UI, needs no new dependency and no native build.
   **Recommended starting point.**
2. A managed TrueType rasteriser (a stb_truetype port, or `SixLabors.Fonts`). Arbitrary sizes,
   one managed dependency, no native build.
3. Compile stb_truetype natively. The NDK *is* installed (see CLAUDE.md, the FNA3D rebuild
   recipe), so this is possible — but it adds a fourth hand-maintained native binary to keep in
   step with vendored C, which the swizzle-patch history argues against.

Option 1 does not preclude 2. Start there, measure how many titles it is insufficient for, and
only then decide.

---

## 4. Staging

The principle: **do not run this as a refactor.** The rasteriser has to grow text and transforms
anyway or Carcassonne and Galactic Reign cannot render. Once it has them, `SilverlightRenderer`
has no capability the rasteriser lacks, and the two paths collapse without a migration.
**The renderer merge is the consequence, not the task.**

### Stage A — grow the rasteriser for titles already on the XNA host

Gaps 1–4, driven by Carcassonne and Galactic Reign. No routing changes, no deletions, nothing
about the Silverlight host touched. Every commit is independently shippable.

*Exit:* Carcassonne and Galactic Reign render their menus correctly on Windows **and Android**,
with an empty `[wpr-uirender]` unsupported list for the screens exercised.

### Stage B — input

Gap 5. Feed `TouchPanel` samples into the existing recogniser and dispatch into the Silverlight
routed-event tree from `MixedModeGame.Update`. Deletes the `InvalidateVisual()` calls — the
rasteriser's signature gate already decides when to repaint.

*Exit:* a mixed-mode title's Silverlight UI takes taps and drags on both heads. Closes the
CLAUDE.md note *"Not built: none of this exists for Silverlight titles."*

### Stage C — flip the two pure-Silverlight titles

Route `ApplicationType.Silverlight` to the same host unconditionally; delete the `isMixedMode`
branch in `ApplicationLaunch` (the `MixedModeDetection.IsMixedMode` call and the "Silverlight UI
runtime is not yet implemented" throw beneath it) and the `IsMixedMode` helper in
`MainWindowDesktop`. Rename `MixedModeGame` — at that point it is simply the Silverlight host.
`MixedModeDetection` can go entirely.

*Exit:* Flowerz and Minesweeper reach the same screens they reach today on the Avalonia host,
**and reach them on Android.** This is the gate; if the rasteriser is not at parity, Stage C
does not start.

### Stage D — delete the Avalonia renderer

| file | lines |
|---|---:|
| `SilverlightRenderer.cs` | 954 |
| `PhoneApplicationFrameView.cs` | 423 |
| `SilverlightLauncher.cs` | 237 |
| `BrandedSplashRenderer.cs` | 189 |
| **subtotal** | **1,803** |

Plus the `Avalonia` package reference in `WPR.Framework.Silverlight.csproj` — which is **already
in the APK today** (the android leg pulls Avalonia 12.1.3 while the desktop leg stays on 11.3.9,
a split the csproj comment explains and apologises for). Removing it deletes that split and
shrinks the APK by an amount not yet measured.

---

## 5. Risks and open questions

**`IBackgroundRenderer` names an Avalonia `DrawingContext` in its signature**, and the whole
`WPR.Backend.Direct3D11` project (562 lines) implements it. Stage D cannot simply delete around
this — the seam needs redesigning to render into the same `Texture2D`, or the D3D11 backend needs
retiring. WP8's `DrawingSurfaceBackgroundGrid` hosts *native* D3D content that WPR cannot execute
anyway, so retiring is plausible, but it is a decision and not a mechanical step. **Decide this
before Stage C, not during Stage D.**

**Text quality will regress on desktop** for the two titles that work today. Avalonia's text
stack is a real one; a baked atlas is not. Acceptable in exchange for those titles existing on
Android at all, but it should be a conscious trade and worth a screenshot comparison at the
Stage C gate.

**Stage C can regress two working titles.** They are the only pure-Silverlight titles ever
tested, so there is no broad regression suite behind them. Keep the Avalonia path behind a flag
for one release rather than deleting it in the same change that flips the routing.

**97 titles is the prize, not the promise.** Nothing here says those titles *work* once they can
launch — only that they cannot launch today. Gap 6 (templates, binding, virtualising lists) is
where most of them will actually stop, and that work is unbounded. The plan's value does not
depend on them: Stages A and B pay for themselves on the twelve mixed-mode titles alone.

---

## 6. The alternative, and why not

**Initialise Avalonia on Android and run the existing Silverlight host there.** Cheaper than it
sounds — `Avalonia.Android` is already referenced by the head, and `WPR.Framework.Silverlight`
already builds an android leg against Avalonia 12.1.3. It is rejected because it solves less:

- Two renderers, two input pipelines and two lifecycles stay, permanently.
- Carcassonne and Galactic Reign are on the *XNA* host and would still be unable to draw their
  Silverlight UI — the rasteriser work in Stage A is needed either way.
- Every XNA-side facility (keyboard-to-touch bindings, wheel scroll, tilt emulation, the graphics
  driver picker and `GraphicsDriverProbe`) continues not to apply to Silverlight titles.

It buys the 97 titles a launch path at the cost of making the split permanent. If the 97 are
wanted urgently and the twelve are not, it is the faster route — but it is a different goal.

---

## 7. Reproducing the measurements

The scans behind §1 are three short Cecil scripts over the installed-game root
(`%LOCALAPPDATA%\WPR\AppData`) or the XAP library (`~\Documents\Windows Phone Games`):

- **per-title Silverlight surface** — read each game's own main assembly, count typerefs whose
  namespace is `WPR.SilverlightCompability` or `System.Windows*` (both appear: the patcher
  rescopes some and not others), then intersect with the hard-feature list. Scanning *every* DLL
  in the install folder instead inflates the count several-fold, because it picks up the WP
  toolkit assemblies the game bundles.
- **mixed-mode across the library** — open each XAP, read `WMAppManifest.xml` for `RuntimeType`,
  and for the Silverlight ones open every `.dll` from the zip stream and test its
  `AssemblyReferences` for `Microsoft.Xna.Framework.Interop`. **Do not test for that DLL's
  presence in the XAP** — it is a phone framework assembly and is never bundled, so a file test
  answers zero for all 307.
- **installed catalogue** — `sqlite3` ships with the Android platform-tools already on this
  machine; `ApplicationType` is `0=XNA 1=Silverlight 2=ModernNative 3=UnityPort`.
