using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Xml.Serialization;
using Mono.Cecil.Rocks;

using System;
using System.IO;
using System.Collections.Generic;
using System.Threading;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using WPR.Common;

namespace WPR
{
    public class ApplicationPatcher
    {
        // Bumped to 2 for the Stage 3 framework rename: the Silverlight shim assembly
        // is now "WPR.Framework.Silverlight" (was "WPR.SilverlightCompability"), so
        // already-installed games carry stale IL scopes and must be reinstalled.
        // Bumped to 14: System.Net.Browser.WebRequestCreator is now redirected to the
        // Silverlight shim. Games installed before this carry IL that still scopes it to
        // System.Windows and must be reinstalled (or repatched) to pick the redirect up.
        // Bumped to 15: the WP7 GraphicsDeviceManager override moved out of the
        // WPR.XnaCompability shim assembly into WPR.Backend.FNA (it subclasses FNA's spine
        // GraphicsDeviceManager, so the backend is its only correct home) and lost its "2"
        // suffix — games are now rescoped to WPR.Backend.FNA.Compat.GraphicsDeviceManager.
        // Version-14 installs still carry IL naming WPR.XnaCompability.GraphicsDeviceManager2
        // and will fail to resolve it until reinstalled. In this version the GraphicsDevice /
        // GraphicsAdapter display-mode overrides only lost their "2" suffix (they subclass
        // WPR-owned types, not FNA, so they stayed put); MemberPatches keys them by typeof, so
        // that rename needed no string change here.
        // Bumped to 16: the WPR.XnaCompability shim assembly is GONE. Its last two types, the WP7
        // display-mode overrides, moved into WPR.Framework.Xna as WPR.Xna.Compat.GraphicsDevice /
        // GraphicsAdapter (they only ever subclassed WPR-owned types), so MemberPatches now rewrites
        // those call sites to an assembly games already bind. Version-15 and older installs carry IL
        // naming WPR.XnaCompability, which no longer ships — they MUST be reinstalled/repatched or
        // they will fail to resolve it at launch.
        // Bumped to 17: the WPR.StandardCompability shim assembly is GONE. Its only type ever, the
        // XElement.Load redirect target, moved to WPR.WindowsCompability.XElement2 to sit with the
        // other BCL-method redirects (Path2 / GC2 / Type2) that MemberPatches already targets.
        // Version-16 and older installs carry IL naming WPR.StandardCompability, which no longer
        // ships. Note this one fails LATE rather than at launch: an unused assembly reference
        // resolves lazily, so an affected game only dies the first time it actually calls
        // XElement.Load. Skulls of the Shogun and Crimson Dragon: Side Story both do.
        // Bumped to 18: the WPR.WindowsCompability shim assembly is GONE. All 17 of its types moved
        // into WPR.Framework.Silverlight, KEEPING the WPR.WindowsCompability namespace — so every
        // NewNamespace string below is unchanged and only the Reference swapped to
        // SilverlightCompRef. Type FullNames are therefore identical; what changed is the assembly
        // that hosts them. Version-17 and older installs carry IL scoping those typerefs to the
        // WPR.WindowsCompability assembly, which no longer ships, so they MUST be
        // reinstalled/repatched. This one fails at LAUNCH, not lazily: System.Windows.Application
        // is on the startup path for Silverlight titles.
        // Bumped to 19: the Microsoft.Xna.Framework.GamerServices assembly is GONE. Its 42 API
        // types moved into WPR.Framework.Xna and GamerServicesComponent (the only FNA-derived one)
        // into WPR.Backend.FNA/Compat/. All types keep their real
        // Microsoft.Xna.Framework.GamerServices namespace.
        //
        // This one is different in kind from 16/17/18. Those dissolved WPR-owned patch targets the
        // game never named. This dissolves an IDENTITY-BINDING assembly: games reference
        // "Microsoft.Xna.Framework.GamerServices, Version=4.0.0.0" by simple name and, until now,
        // the patcher deliberately did NOT rename that ref — our assembly carried the WP7 identity
        // so it bound directly. The ref is now rewritten to WPR.Framework.Xna instead, which means
        // ANY version-18-or-older install fails at launch. All 16 test installs named it.
        // See Plans/ARCHITECTURE-MIGRATION.md §3.2 — this deliberately departs from the
        // "one assembly = one identity" rule recorded there.
        // Bumped to 20: every IsolatedStorageFile.OpenFile / CreateFile call site is rewritten to
        // WPR.WindowsCompability.SharedIsolatedStorage, which opens with FileShare.ReadWrite. See
        // RedirectIsolatedStorageCalls. Unlike 19 this is not identity-binding — an older install
        // still launches, it just keeps the exclusive share and therefore keeps failing to save if
        // the game leaks a handle. Reinstall (or repatch) to pick it up.
        // Bumped to 21: spine relocation step 2 — Game, GameComponent, DrawableGameComponent,
        // GameServiceContainer and GameWindow moved from the FNA backend into WPR.Framework.Xna,
        // and WprFrameworkXnaTypes now rescopes games' refs there. This is IDENTITY-BINDING and
        // therefore hard: a version-20 install carries IL naming [FNA]Microsoft.Xna.Framework.Game,
        // FNA no longer defines it, and the game will TypeLoadException at launch. Unlike v20 this
        // is NOT optional — every installed game must be repatched or reinstalled.
        // (--repatch-installed is enough; it restores each .dll.original first, so it is idempotent.)
        //
        // The transitional TypeForwardedTo that step 1 left in FNA for GameWindow was deleted in the
        // same change; this rescope replaces it. Do not re-add one — a forwarder plus a rescope means
        // two ways to resolve the same type, and the failure mode (a game binding the forwarder while
        // the patcher table says otherwise) is invisible until a cast fails at runtime.
        //
        // Bumped to 22: typeof() arguments inside CUSTOM ATTRIBUTE BLOBS are now rescoped too
        // (RescopeCustomAttributeTypeArguments). They are stored as assembly-qualified STRINGS, not
        // as TypeRef table rows, so every redirect above had been skipping them since forever — an
        // attribute kept naming a WP7 assembly that no longer exists. Not identity-binding: a v21
        // install still launches, it just keeps failing to build any XmlSerializer over the
        // affected type. Repatch (or reinstall) to pick it up.
        //
        // Bumped to 23: game file I/O now goes through WPR.Engine.Content, so a hardcoded Windows
        // path like "Content\Credits.xml" opens on Android instead of naming a single file with a
        // backslash in it. Affects every path-taking System.IO / System.Xml member a game calls
        // (see the MemberPatches block). Not identity-binding: a v22 install still launches, it
        // just keeps failing those opens — and because games swallow the exception the symptom
        // shows up somewhere unrelated (Battlewagon: no menu, 5,593 NREs a run). Repatch is
        // enough; no reinstall needed.
        //
        // Bumped to 24: no table changed — what changed is that assemblies which previously
        // FAILED to patch now succeed. Cecil resolves a constant's declared type while writing
        // the Constant table (MetadataBuilder.GetConstantType), which on Android always failed
        // because no managed assembly is on disk there; PatchDll logged it and left that DLL
        // unpatched, so the game still bound the WP7 XNA identities and died at launch with a
        // FileNotFoundException inside an AggregateException. ConstantEnumStubResolver answers
        // that resolve from the constant's own recorded value. The bump exists purely so those
        // installs repatch themselves — an affected DLL is pristine on disk, not stale, and
        // nothing else can tell the difference. Repatch is enough; no reinstall needed.
        // Measured on a 36-game phone: Beards and Beaks and Chickens Can't Fly each had their
        // MAIN assembly skipped and neither would start.
        //
        // Bumped to 25: ApplyGameSpecificFixups gained a second entry — Feed Me Oil's
        // OggSound.StopById is rewritten to guard its unconditional RemoveAt(found), which is
        // what froze the game (input included) on its first change of music. This DOES rewrite
        // game IL, so an install made before it keeps the old body and keeps freezing.
        // Not identity-binding — a v24 install still launches — so repatch is enough.
        //
        // Bumped to 26: WprFrameworkXnaTypes gained Microsoft.Xna.Framework.Media.Playlist and
        // PlaylistCollection, the only two types of that namespace WPR had never defined. Without
        // the entries a game's typeref stays scoped to FNA, which defines no XNA API at all, so
        // the load throws TypeLoadException. Fast and the Furious: Adrenaline asks for the
        // playlist count from a constructor fourteen objects deep, so that throw unwound its whole
        // application object and left the Oberon SDK's PlatformStub.Draw NREing on every frame
        // with nothing drawn — reported as a white screen on load. IS identity-binding for any
        // game that touches playlists (a v25 install carries IL naming [FNA]…PlaylistCollection),
        // but repatch is still enough: --repatch-installed rescopes the typeref in place.
        //
        // Bumped to 27: RelocateMonoStackConflictBlocks, a new IL pass that works around MonoVM's
        // IL importer carrying the stack state across an unconditional branch into the block that
        // follows it. Affects ANDROID ONLY at runtime — CoreCLR compiles the original shape fine —
        // but the patched install is shared between the heads, so the pass runs unconditionally.
        // This DOES rewrite game IL, so an install made before it keeps the old bodies and keeps
        // throwing InvalidProgramException with an empty message out of whichever method hits the
        // shape. Not identity-binding — a v26 install still launches — so repatch is enough.
        //
        // Bumped to 28: the same pass now also relocates blocks that end in br, not just ret and
        // throw. The br case is not a corner — Earthworm Jim's b2PolygonShape::.ctor has four
        // conflicting blocks and ALL FOUR end in br, so v27 left that method untouched.
        //
        // Bumped to 29: the v28 br extension is OFF by default again (see MonoRelocationBrEnabled),
        // so v29 output is byte-for-byte v27 output. Measured 2026-09-20 on the emulator with a
        // RELEASE APK — i.e. the Mono JIT, which is what every shipped build runs — Earthworm Jim:
        //   v21 IL (0.1.03)            -> title, menu, level 1 plays
        //   v28 IL, pass disabled       -> title, menu, level 1 plays
        //   v28 IL, br extension off    -> title, menu, level 1 plays   (this is v29 / v27 output)
        //   v28 IL as shipped           -> never leaves the Gameloft logo; no exception logged
        // Final Fantasy III's IL is identical for v21, v27 and v29 and differs only under v28
        // (three br blocks), so the same regression covers it. The InvalidProgramException the
        // v27/v28 notes chased is a property of the Mono INTERPRETER, which only a Debug APK
        // runs: the identical v21 IL throws it on every frame under a Debug build and plays
        // under a Release build of the same tree. Rewrites game IL, not identity-binding, so
        // --repatch-installed is enough; a v28 install keeps the br clones and keeps hanging
        // until it is repatched.
        //
        // Bumped to 30: the WP7.1 Silverlight/XNA MIXED-MODE surface. WprFrameworkXnaTypes gained
        // GameTimer, GameTimerEventArgs, SharedGraphicsDeviceManager and
        // Graphics.GraphicsDeviceExtensions; Patches gained MediaElement and its supporting media
        // types. Two of the four XNA types came from Microsoft.Xna.Framework.Interop, an assembly
        // WPR has no counterpart for, so without these entries their typerefs kept the blanket
        // Microsoft.Xna.* -> FNA rename and resolved to nothing.
        //
        // This is what makes a mixed-mode title launchable at all: such an app has NO Game
        // subclass, so before this the launcher classified it by its manifest's
        // RuntimeType="Silverlight" and refused it outright. 13 titles in a 307-XAP library use
        // this model, 10 of them with no other blocker — Cut the Rope and Cut the Rope:
        // Experiments, Big Buck Hunter Pro, Carcassonne, Flight Control Rocket, Galactic Reign,
        // Little Acorns, Rabbids Go Phone, Sid Meier's Pirates! and The Game of Life.
        //
        // IS identity-binding for any of them (a v29 install carries IL naming
        // [FNA]…SharedGraphicsDeviceManager), but no IL body is rewritten, so
        // --repatch-installed is enough and nothing needs a reinstall. Every other title is
        // unaffected: none of these names appears in a game that does not use the model.
        //
        // Bumped to 31: RedirectIsolatedStorageCalls (renamed from ...Opens) now covers EVERY
        // path-taking IsolatedStorageFile member, not just OpenFile/CreateFile, and every one of
        // them normalises its path or search pattern through ContentPaths. v20 added the rewrite
        // for FileShare reasons and needed only the two opening members; the separator problem is
        // unrelated and had therefore never been fixed for the other thirteen.
        //
        // This is the v23 defect class — a WP7 title spelling a path with a Windows separator,
        // which Android treats as an ordinary filename character — on the one type v23 could not
        // reach, because IsolatedStorageFile is sealed and MemberPatches needs a substitutable
        // replacement. Funny Bounce (f84a19d8-2820-41a6-b972-1f0c7da88196) is the reference case:
        // GetFileNames("GameData_x\\*") matched nothing, so its high-score list came back empty,
        // GameOverScreen.LoadContent threw out of .First(), and the half-built screen then drew
        // nothing but its clear colour on every frame thereafter — reported as issue #41's "blue
        // screen after game over", Android only.
        //
        // Rewrites game IL and is NOT identity-binding: a v30 install still launches, it just
        // keeps addressing paths that do not exist on a phone. --repatch-installed is enough
        // (automatic on next launch on Android); nothing needs a reinstall. Windows is unchanged
        // either way, because ContentPaths.Normalize is a no-op where '\' is already native.
        //
        // Bumped to 32: System.Windows.Controls.ProgressBar and its RangeBase now have shims and
        // rescope entries. ProgressBar was the LAST System.Windows control a WP7 loading screen
        // routinely builds that WPR had no type for, and a missing type is resolved when the
        // method naming it is compiled — so the whole of Rabbids Go Phone's
        // Screens.Loading.LoadContent failed, the screen was never added, and tapping "My Rabbid"
        // on the main menu did nothing at all with nothing on screen to say why. The type also
        // appears in that game's VideoGallery and its AR page.
        //
        // Not identity-binding (a v31 install launches; it just keeps failing on the screens that
        // need a ProgressBar), and no IL body is rewritten, so --repatch-installed is enough and
        // nothing needs a reinstall.
        // Bumped to 33: PreserveOriginalMetadataTokens, a new pass that gives a game back the
        // metadata tokens its PRISTINE assembly carried. Cecil does not preserve TypeDef row ids —
        // it re-emits the table depth first, every type immediately followed by its nested types,
        // so an assembly laid out any other way is renumbered even though nothing about its types
        // changed. Ordinary game code cannot tell; Eazfuscator.NET can, because it derives its
        // string-decryption key from the tokens of its own helper types and uses the result as a
        // byte offset into the encrypted string blob. The Treasures of Montezuma
        // (56a2bd8b-af90-4575-b25f-97b31a179422) is the reference case: every launch died in
        // Game..ctor with "ArgumentOutOfRangeException: value ('-180695550') must be a
        // non-negative value" out of UnmanagedMemoryStream.set_Position, on both heads, before a
        // frame was drawn. Only TWO games in a 307-XAP library ask for a token outside
        // UnityEngine — this one and Farm Frenzy 2, the same Alawar/YF engine, eight obfuscated
        // assemblies each — so the pass is a no-op everywhere else.
        //
        // Rewrites game IL and embeds a resource, so a v32 install keeps the broken key and keeps
        // throwing. Not identity-binding — a v32 install still launches — so repatch is enough.
        //
        // Bumped to 34: System.Windows.Controls.Viewbox now has a shim and a rescope entry. It is
        // the layout container WP7 designs use to fit one layout to more than one resolution, and
        // a page naming it in XAML also gets an x:Name'd field for it in the generated
        // InitializeComponent — so the missing type was resolved when that method was COMPILED and
        // failed the whole page, not the one container. Found in Carcassonne, which names it 26
        // times across its main menu; it is ordinary Silverlight and is expected in others.
        //
        // Not identity-binding (a v33 install launches; it just fails the pages that use a
        // Viewbox) and no IL body is rewritten, so --repatch-installed is enough and nothing needs
        // a reinstall.
        //
        // Bumped to 35: NeutraliseObfuscatorStackIdentityChecks, a new pass that makes
        // Eazfuscator.NET's caller-identity checks answer "trusted". The decryptor walks
        // StackTrace to a FIXED frame index and demands that frame's declaring type live in its
        // own assembly; one check feeds a poison flag that collapses EVERY string in the assembly
        // to the literal "X0X", the other feeds the decryption key itself. On CoreCLR-for-Android
        // the reported stack is not what it expects, so The Treasures of Montezuma
        // (56a2bd8b-af90-4575-b25f-97b31a179422) died in Game..ctor with "An item with the same
        // key has already been added. Key: X0X" — two different axis-id strings having both
        // decrypted to the sentinel.
        //
        // Distinct from the v33 pass despite the same obfuscator and the same game: v33 repairs a
        // key input WPR itself perturbs (Cecil renumbers the TypeDef table), this one a check WPR
        // does not touch — proven by running byte-identical patched assemblies on both heads, one
        // of which plays and one of which poisons. Desktop is unaffected either way, because the
        // pass writes the answer the check already reaches there.
        //
        // Rewrites game IL, so a v34 install keeps the poisoned strings. Not identity-binding — a
        // v34 install still launches — so --repatch-installed is enough on the desktop and Android
        // repatches itself on next launch. Of 48 installed titles only Montezuma carries the
        // check; Farm Frenzy 2 is the likely second, being the same Alawar/YF engine.
        // Bumped to 36: System.Windows.Media.RotateTransform and ScaleTransform now have shims and
        // rescope entries. They were the two most-used transforms in WP7 XAML and the only two of
        // the family with no entry, so their typerefs kept WP7's System.Windows scope — which does
        // not exist at runtime, so the whole method naming one failed to load rather than merely
        // losing a transform. Galactic Reign (45859ddf-684e-43bc-a282-0a4494e88864) is the measured
        // case: TypeLoadException on RotateTransform took ArmadaClient.MenuPage's construction with
        // it and the game never reached a menu.
        //
        // Neither transform is APPLIED by either renderer yet — the CPU rasteriser is axis-aligned
        // — so this buys the page loading, not the rotation. See
        // Plans/SILVERLIGHT-XNA-CONVERGENCE.md, gap 2.
        //
        // Not identity-binding (a v35 install launches; it just fails the pages that use one) and
        // no IL body is rewritten, so --repatch-installed is enough and nothing needs a reinstall.
        // Bumped to 37: the Button family now matches Silverlight's real hierarchy —
        // ContentControl -> ButtonBase -> Button, and ButtonBase -> ToggleButton ->
        // CheckBox/RadioButton — with ButtonBase and RadioButton added to this table. It was flat
        // before, each type deriving straight from ContentControl with its own copy of the shared
        // members, which is wrong twice over: ButtonBase and RadioButton did not exist as names at
        // all, and Click was declared on Button rather than on ButtonBase where Silverlight
        // declares it, so `button.Click += h` (which compiles to ButtonBase::add_Click) could not
        // resolve. The same lesson as RangeBase/ProgressBar at v32: IL names the DECLARING type.
        //
        // Galactic Reign (45859ddf-684e-43bc-a282-0a4494e88864) is the measured case: it names
        // ButtonBase and RadioButton, and the TypeLoadException took ArmadaClient.MenuPage down
        // before the game issued a single draw call.
        //
        // Not identity-binding (a v36 install launches; it just fails the pages that use one) and
        // no IL body is rewritten, so --repatch-installed is enough and nothing needs a reinstall.
        // Bumped to 38: every IsolatedStorageFile.GetUserStoreForApplication() call site now goes
        // to SharedIsolatedStorage.GetUserStoreForApplication, which hands each game its own store
        // (PerGameIsolatedStorage) instead of the one the BCL keys on the host exe. Before this,
        // every game shared a store and same-named files collided: Fragger, Monster Island and
        // iStunt 2 write $_StatesAfterExitData_$\DLCManager in a shape Gravity Guy
        // (4f930d12-2350-4c01-91e8-f46b8bd1d884) cannot read, and playing any of them once left
        // Gravity Guy drawing nothing on every later launch.
        //
        // Existing saves are copied into each installed game's new store on its first launch, so
        // nothing is lost. Not identity-binding (a v37 install launches, still on the shared store),
        // so --repatch-installed is enough; Android repatches on next launch.
        // Bumped to 39: ApplyGameSpecificFixups gained a third entry. The Treasures of Montezuma's
        // options dialog zeroes the music slider whenever MediaPlayer is paused, and the game
        // pauses it itself for every level-start jingle, so the music turned itself off at the
        // start of each level. The handler is emptied. Rewrites game IL, so a v38 install keeps
        // the old handler; not identity-binding.
        // Bumped to 40: Monitor.Enter (every C# `lock`) in game IL now goes to
        // WPR.Xna.Compat.Monitor. On the OpenGL driver a worker thread's draw calls are run on the
        // device thread (WPR.Xna.Rhi.DeviceThreadDispatch), and the device thread has to keep
        // servicing them while it waits for a game lock the worker holds, or the two deadlock.
        // Plants vs. Zombies (706f822a-a47e-e011-986b-78e7d1fa76f8) is the reference case: its
        // loader draws into render targets under ResourceManager.DrawLocker, which Main.Draw also
        // takes. Off OpenGL the shim is a plain Monitor.Enter. Not identity-binding; a v39 install
        // still launches, so --repatch-installed is enough and Android repatches on next launch.
        // Bumped to 41: the Silverlight shapes and geometry types (Ellipse, Line, Polyline,
        // Polygon, Path, PathGeometry and its figures/segments, EllipseGeometry, LineGeometry,
        // GeometryGroup, PointCollection, DoubleCollection, FillRule, SweepDirection, PenLineJoin,
        // PenLineCap) now have shims and rescope entries. Before this their typerefs kept WP7's
        // System.Windows scope, so a page whose generated InitializeComponent names an x:Name'd
        // Path failed to compile as a whole. Flowerz (981750c8-24cc-df11-9eae-00237de2db9e) is the
        // measured case: 51 named Paths. Not identity-binding; --repatch-installed is enough and
        // Android repatches on next launch.
        // Bumped to 42: System.Windows.Input.MouseButtonEventHandler, MouseEventArgs and
        // MouseEventHandler gained rescope entries (the shims already existed), plus the WP7
        // NetworkInformation name-resolution types. Pure Silverlight titles now run on the XNA
        // host (convergence plan Stage C), and Flowerz's game board subscribes
        // MouseLeftButtonDown in its constructor, so with the WP7 scope left on the delegate the
        // game screen could never be built. Not identity-binding; --repatch-installed is enough
        // and Android repatches on next launch.
        // Bumped to 43: an assembly reference to "mscorlib, Version=255.255.255.255" (the WinRT
        // metadata pseudo-reference, which WinmdStubber used to copy into every .winmd stub) is
        // retargeted to the mscorlib 4.0.0.0 facade. Unbindable otherwise, so any WP8 managed
        // title touching a stubbed WinRT component (Microsoft.Xbox, SharpDX.WP8) failed with
        // FileNotFoundException; Gravity Guy 2 (eaa1ee62-073a-4520-831f-08691889f7f1) is the
        // measured case. Not identity-binding; --repatch-installed is enough (it patches the
        // stub's .dll.original) and Android repatches on next launch.
        // Bumped to 44: an assembly reference to WP7's "mscorlib.Extensions" is retargeted to the
        // runtime's mscorlib 4.0.0.0 facade instead of System.Runtime, which does not forward
        // System.Threading.Mutex. AE Fruit Slash (0456cf7d-1802-4e07-8120-304a06cbdbf1, the
        // AEMobile framework) threw TypeLoadException in AEGame..ctor. Not identity-binding;
        // --repatch-installed is enough and Android repatches on next launch.
        // Bumped to 45: Windows.Foundation's async family (IAsyncAction, IAsyncOperation`1, their
        // handlers, AsyncStatus) and mscorlib's EventRegistrationToken / WindowsRuntimeMarshal are
        // rescoped to System.Runtime.WindowsRuntime (WPR.Framework.WinRT), which implements them;
        // System.Runtime.WindowsRuntime references are normalised to 4.0.0.0. That is what lets a
        // managed WP8 title's Xbox Live calls reach the Microsoft.Xbox facade (WPR.Framework.Xbox,
        // WPR Hub) and complete: Gravity Guy 2 sets IAsyncAction.Completed in MainPage's ctor.
        // Not identity-binding; --repatch-installed is enough and Android repatches on next launch.
        // Bumped to 46: Windows.ApplicationModel.Package, Windows.Storage.StorageFolder and
        // Windows.Graphics.Display's DisplayProperties / ResolutionScale / DisplayOrientations join
        // WinRtProjectionTypes. WPR.Framework.WinRT implements them; before, they bound per-game
        // empty shells (a shell class for an enum, a Package with no Current), and
        // SharpDX.Toolkit.Game's boot read Package.Current.InstalledLocation.Path for its content
        // root: Gravity Guy 2's MainPage failed there. Not identity-binding.
        // Bumped to 47: every assembly reference loses ContentType=WindowsRuntime in the patcher
        // itself. WinRtRefStripper did it only at install, on the live DLL, and its Cecil write
        // threw wherever WPR's DLLs were not in the working directory (always on Android), so
        // a WP8 managed title kept WinRT-flagged refs and threw PlatformNotSupported in every
        // method naming Windows.* or Microsoft.Xbox (Microsoft Mahjong's Arkadium.Common.WP8).
        // Not identity-binding; --repatch-installed is enough and Android repatches on launch.
        // Bumped to 48: a typeref into Silverlight's System.Windows assembly that Patches does not
        // list binds the shim of the same name (SilverlightShimTypes). Arkadium.Common.WP8 named
        // ControlTemplate, Setter and RoutedPropertyChangedEventArgs`1, whose shims existed with no
        // table entry; GamePanoramaControl's static constructor threw, and Microsoft Solitaire's
        // main page lost its panorama. Not identity-binding.
        // Bumped to 49: no table changed, but SilverlightShimTypes gained DiscreteDoubleKeyFrame
        // (and RenderingEventArgs), which only a repatch binds. Microsoft Minesweeper's difficulty
        // tiles threw TypeLoadException on every tap. Not identity-binding.
        // Bumped to 50 (written as 47 on branch toolkit-gestures): the WP Toolkit gesture redirects are gone, so a game's own
        // Microsoft.Phone.Controls.Toolkit.dll gesture engine runs (see the note where the entries
        // were). The redirected typerefs were dangling (scoped to the game module), so every
        // GestureListener handler failed to bind: Minesweeper (0b00c4a3-eda9-e011-a53c-78e7d1fa76f8)
        // and Sudoku took no menu taps, and Hold/Drag/Pinch never reached any game. Also
        // System.Windows.Media.Matrix / MatrixTransform, which the Toolkit's drag code casts to.
        // Not identity-binding; --repatch-installed is enough and Android repatches on next launch.
        // Bumped to 51: no table changed, but SilverlightShimTypes gained SkewTransform, which game
        // IL binds only on a repatch (XAML resolves it without one). Not identity-binding.
        // Bumped to 52: no table changed, but SilverlightShimTypes gained VirtualizingStackPanel
        // and VirtualizationMode, which game IL binds only on a repatch. PointAbout Mashup (Fox
        // News Latest Headlines, 3c10c9db-9a3d-4b7f-8794-cf023e3d8a32) names VirtualizingStackPanel
        // from its bundled LazyListBox, which then failed to load. Not identity-binding.
        // Bumped to 53: WP8's Microsoft.Xna.Framework.Media.PhoneExtensions.MediaLibraryExtensions
        // joins WprFrameworkXnaTypes (it used to fall to the blanket Microsoft.Xna -> FNA rescope,
        // and FNA defines no XNA API, so YouTube (dcbb1ac6-a89a-df11-a490-00237de2db9e) died with
        // an unhandled TypeLoadException), and WPR.Framework.WinRT gained ApplicationProfile,
        // VoiceCommandService and Windows.System.Threading.ThreadPool, which game IL binds only on
        // a repatch. Not identity-binding; --repatch-installed is enough and Android repatches on
        // next launch.
        // Bumped to 54: System.Windows.Threading.DispatcherSynchronizationContext joins the table
        // (the Silverlight host now installs one on its thread, so BackgroundWorker completion and
        // await continuations return to the UI thread). Not identity-binding.
        // Bumped to 55 (written as 54 on 0.2.07, a number branch xbox-live-hub had also taken):
        // every Socket.ConnectAsync / SendAsync / ReceiveAsync / SendToAsync / ReceiveFromAsync call site goes to WPR.WindowsCompability.SilverlightSockets, which keeps
        // Silverlight's "always completes asynchronously" contract (see RedirectSocketCalls). Pool
        // Pro Online 3 hung for ever on "Connecting to server" without it. Rewrites game IL, so a
        // v54 install keeps hanging until repatched; not identity-binding.
        // Bumped to 56: SocketAsyncEventArgs.Completed += / -= go to SilverlightSockets.AddCompleted /
        // RemoveCompleted, which run a receive's callback between the game's frames
        // (GameFrameGate). Pool Pro Online 3's receive callback raced its frame on a multi-core
        // machine and lost packets, hanging matches on "waiting for player". Rewrites game IL;
        // not identity-binding.
        // Bumped to 57 (written as 56 on branch ae-fruit-slash, a number 0.2.07 had also taken): every Module.get_Name call site goes to
        // WPR.WindowsCompability.ReflectionModules.GetName (RedirectModuleNameCalls): a
        // stream-loaded module is "<Unknown>" to .NET and was its file name on the phone. AE Fruit
        // Slash (0456cf7d-1802-4e07-8120-304a06cbdbf1) never saved anything without it. And
        // Assembly.Load(string) goes to WPR.WindowsCompability.Assembly2, which hands back an
        // assembly already loaded in any context (the ad SDK's AdManager..cctor asks for the
        // entry assembly from the Default context). Rewrites game IL; not identity-binding.
        // Bumped to 58 (written as 57 on branch ae-fruit-slash): named Mutex constructors and Mutex.OpenExisting go to
        // WPR.WindowsCompability.NamedMutex (RedirectNamedMutexCalls). Android cannot create a
        // named mutex (the runtime's shared-memory directory is /data/local/tmp), so AE Fruit
        // Slash died in AEGame..ctor on a phone. Rewrites game IL; not identity-binding.
        // Bumped to 59: Windows.ApplicationModel.Store's LicenseInformation, ListingInformation,
        // ProductLicense, ProductListing and ProductType joined WPR.Framework.WinRT
        // (WinRtImplementedTypes), so their typerefs leave the per-game synthesized Windows.dll.
        // iRunner's bundled StoreWrapper.dll stopped its first scene on CurrentApp's missing
        // LoadListingInformationAsync. And Deployment.Parts is Silverlight's AssemblyPartCollection of
        // AssemblyPart (it was DeploymentPartCollection, a name no game binds): OpenXLive enumerates it.
        // Not identity-binding.
        // Bumped to 60 (trial, branch xna-facades): Microsoft.Xna.* references are no longer
        // renamed or rescoped. Src/Facades/Xna builds an assembly of each WP7/WP8 XNA name that
        // type-forwards to WPR.Framework.Xna (and GamerServicesComponent to WPR.Backend.FNA,
        // UIElementRenderer to WPR.Framework.Silverlight), so a game binds them by its own
        // references. GraphicsDeviceManager keeps its Patches entry (it changes namespace).
        // IDENTITY-BINDING: a v59 install names [WPR.Framework.Xna]/[FNA] for XNA types and still
        // works; a v60 install names Microsoft.Xna.* and needs the facades present.
        // Bumped to 61 (branch xna-facades): the Silverlight shims carry Silverlight's own
        // namespaces (System.Windows.Controls.Button, not WPR.SilverlightCompability.Button), and a
        // game's reference to Silverlight's System.Windows is renamed to WPR.Framework.Silverlight
        // as a whole (IsSilverlightSystemWindows). The ~250 per-type Patches entries and the
        // SilverlightShimTypes by-name table are gone: a type binds by its own name, so a shim
        // written later needs neither a table entry nor a repatch. Types WP7 kept in
        // Microsoft.Phone (Frame, Page, the navigation types, Stopwatch) are forwarded by WPR's
        // Microsoft.Phone. IDENTITY-BINDING: every Silverlight and mixed-mode install repatches.
        // Branch 0.2.09 numbered its own 60-62 in parallel, so installs repatched by an 0.2.09 build
        // carry 62 too. Its changes, now ported here and covered by the bump to 63:
        //   60: System.Net.Sockets.UdpAnySourceMulticastClient joins Patches (.NET has no such type;
        //       PAC-MAN Kart Rally's "Host game" threw TypeLoadException from its channel ctor).
        //   61: INotifyPropertyChanging, PropertyChangingEventHandler and PropertyChangingEventArgs
        //       bind to System.ObjectModel, and Type.GetType(string) goes to Type2 like the
        //       two-argument overload (Microsoft Bingo's MvvmLight; its navigation graph got
        //       FileLoadException 0x80131515 from the second).
        //   62: no per-game IL fixups (.claude/rules/no-per-game-rules.md): Hoth's reveal rewrite and
        //       Feed Me Oil's StopById guard are gone, Montezuma's music-slider fixup is a pattern
        //       with no title gate (EmptyPauseMirroringMediaStateHandlers).
        // Bumped to 63: all of the above, plus this branch's WinRT additions:
        // Windows.ApplicationModel.Activation (ContractActivated's IActivatedEventArgs), Windows.Web.Http,
        // Security.Credentials, Data.Xml.Dom, Storage.Streams.DataReader/Writer and the rest of Live
        // Store's surface joined WPR.Framework.WinRT, so WinRtImplementedTypes rescopes them.
        // Above every 0.2.09 install's 62, so those repatch too. Not identity-binding.
        // Bumped to 64: Windows.Foundation.Collections.IPropertySet and PropertySet joined
        // WPR.Framework.WinRT (CoreApplication.Properties is typed as it, as game IL binds the getter),
        // so WinRtImplementedTypes rescopes them. Not identity-binding.
        // Bumped to 65: Windows.Phone.System.Analytics.HostInformation joined WPR.Framework.WinRT
        // (Asphalt 7's page reads PublisherHostId), so WinRtImplementedTypes rescopes it.
        // Not identity-binding.
        public static int Version => 65;

        private AssemblyNameReference FnaBackendRef;
        private AssemblyNameReference FNARef;
        private AssemblyNameReference SystemRunTimeRef;
        private AssemblyNameReference SilverlightCompRef;
        private AssemblyNameReference MicrosoftPhoneRef;
        // Stage 5a: the XNA value/math types are owned by WPR.Framework.Xna (pulled out of FNA).
        // Game typerefs to those types are rescoped straight here — no FNA forwarder needed.
        private AssemblyNameReference WprFrameworkXnaRef;
        private AssemblyNameReference ServiceModelPrimitivesRef;
        private AssemblyNameReference ServiceModelHTTPRef;
        private AssemblyNameReference JsonSerializationRef;
        private AssemblyNameReference ObjectModelRef;

        private class TypePatchInfo
        {
            public String? NewName;
            public String? NewNamespace;
            public AssemblyNameReference? Reference;
        }

        private Dictionary<string, TypePatchInfo> Patches;
        private Dictionary<string, Type> MemberPatches;

        /// <summary>
        /// WinRT types WPR implements for real in WPR.Framework.WinRT (assembly
        /// <c>System.Runtime.WindowsRuntime</c>): Windows.Foundation's async family, which WP8 user
        /// IL scopes to <c>Windows</c> (per-device Windows.winmd), and the two interop types WP8
        /// kept in mscorlib. Their typerefs are rescoped there, wherever they point, because
        /// otherwise they bind WindowsTypeSynthesizer's per-game empty shells (no members, so
        /// <c>IAsyncAction.put_Completed</c> is a MissingMethodException) or nothing at all (.NET 5
        /// removed EventRegistrationToken and WindowsRuntimeMarshal). Version 45.
        /// </summary>
        internal static readonly HashSet<string> WinRtProjectionTypes = new(StringComparer.Ordinal)
        {
            "Windows.Foundation.AsyncStatus",
            "Windows.Foundation.IAsyncInfo",
            "Windows.Foundation.IAsyncAction",
            "Windows.Foundation.IAsyncOperation`1",
            "Windows.Foundation.IAsyncActionWithProgress`1",
            "Windows.Foundation.IAsyncOperationWithProgress`2",
            "Windows.Foundation.AsyncActionCompletedHandler",
            "Windows.Foundation.AsyncOperationCompletedHandler`1",
            "Windows.Foundation.AsyncActionProgressHandler`1",
            "Windows.Foundation.AsyncActionWithProgressCompletedHandler`1",
            "Windows.Foundation.AsyncOperationProgressHandler`2",
            "Windows.Foundation.AsyncOperationWithProgressCompletedHandler`2",
            "System.Runtime.InteropServices.WindowsRuntime.EventRegistrationToken",
            "System.Runtime.InteropServices.WindowsRuntime.EventRegistrationTokenTable`1",
            "System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeMarshal",
            // Version 46: the package and display classes WP8 SharpDX titles read at boot.
            "Windows.ApplicationModel.Package",
            "Windows.Storage.StorageFolder",
            "Windows.Graphics.Display.DisplayProperties",
            "Windows.Graphics.Display.ResolutionScale",
            "Windows.Graphics.Display.DisplayOrientations",
        };

        /// <summary>
        /// Every public top-level type WPR.Framework.WinRT defines, by full name: a typeref to any of
        /// them is rescoped there, like <see cref="WinRtProjectionTypes"/>, which it supersedes as
        /// the source of truth (the hand-kept list stays for the types WP8 kept in mscorlib, whose
        /// names would match anyway). Implementing a WinRT class in that assembly is therefore all
        /// it takes for game IL to bind it. Version 47: the storage, stream, network, Online ID and
        /// pointer/gesture classes the Microsoft casual games (Arkadium) use.
        /// </summary>
        internal static readonly HashSet<string> WinRtImplementedTypes = BuildWinRtImplementedTypes();

        private static HashSet<string> BuildWinRtImplementedTypes()
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            // WinRtHost, not a Windows.* type: on Windows the SDK projection defines those names too.
            foreach (Type t in typeof(WPR.WinRT.WinRtHost).Assembly.GetExportedTypes())
            {
                if (!t.IsNested && t.FullName != null && !(t.Namespace ?? string.Empty).StartsWith("WPR.", StringComparison.Ordinal))
                {
                    names.Add(t.FullName);
                }
            }

            return names;
        }

        /// <summary>Silverlight's public key token, which its System.Windows (2.0.5.0, WP8's 2.0.6.0) carries.</summary>
        private static readonly byte[] SilverlightPublicKeyToken = { 0x7c, 0xec, 0x85, 0xd7, 0xbe, 0xa7, 0x79, 0x8e };

        /// <summary>A reference to Silverlight's System.Windows, as opposed to .NET's facade of the same name.</summary>
        internal static bool IsSilverlightSystemWindows(AssemblyNameReference refer) =>
            string.Equals(refer.Name, "System.Windows", StringComparison.OrdinalIgnoreCase)
            && refer.PublicKeyToken is { Length: 8 } token
            && token.AsSpan().SequenceEqual(SilverlightPublicKeyToken);

        /// <summary>The identity WPR.Framework.WinRT is built as; see <see cref="WinRtProjectionTypes"/>.</summary>
        internal const string WinRtProjectionAssembly = "System.Runtime.WindowsRuntime";

        /// <summary>Namespaces the patcher's by-name rules redirect (XNA, Silverlight, the phone, WinRT).</summary>
        private static readonly System.Text.RegularExpressions.Regex FrameworkNamespace =
            new(@"^(System|Microsoft\.(Xna|Phone|Devices)|Windows)(\.|$)", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>
        /// Assembly names that are copies of the platform (or of toolkits WPR replaces), never a
        /// package's own implementation, even when a package ships one: Pool Pro bundles
        /// System.Windows.dll and Microsoft.Phone.dll reference copies, and CleanupUnreferencedBundledDlls
        /// relies on refs into Microsoft.Expression.Interactions being redirected.
        /// </summary>
        private static readonly System.Text.RegularExpressions.Regex FrameworkAssemblyName =
            new(@"^(System|mscorlib|Microsoft\.(Xna|Phone|Devices|Xbox|Expression|Advertising)|Windows)(\.|$)",
                System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        private readonly Dictionary<string, HashSet<string>> _packageAssembliesByFolder = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// True when <paramref name="assemblyName"/> is one of the package's own assemblies (a
        /// .dll beside <paramref name="modulePath"/>, or its pristine .dll.original) and not a
        /// framework copy. A typeref scoped to it names code the package ships.
        /// </summary>
        private bool PackageOwnsScope(string modulePath, string assemblyName)
        {
            if (FrameworkAssemblyName.IsMatch(assemblyName)) return false;
            string folder = Path.GetDirectoryName(modulePath) ?? string.Empty;
            if (!_packageAssembliesByFolder.TryGetValue(folder, out HashSet<string>? names))
            {
                names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (string file in Directory.EnumerateFiles(folder))
                    {
                        string name = Path.GetFileName(file);
                        if (name.EndsWith(".dll.original", StringComparison.OrdinalIgnoreCase)) name = name[..^".original".Length];
                        if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) names.Add(name[..^4]);
                    }
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
                _packageAssembliesByFolder[folder] = names;
            }
            return names.Contains(assemblyName);
        }

        public ApplicationPatcher()
        {
            FNARef = AssemblyNameReference.Parse("FNA");
            FnaBackendRef = AssemblyNameReference.Parse("WPR.Backend.FNA");
            SystemRunTimeRef = AssemblyNameReference.Parse("System.Runtime");
            SilverlightCompRef = AssemblyNameReference.Parse("WPR.Framework.Silverlight");
            MicrosoftPhoneRef = AssemblyNameReference.Parse("Microsoft.Phone");
            WprFrameworkXnaRef = AssemblyNameReference.Parse("WPR.Framework.Xna");

            ServiceModelPrimitivesRef = AssemblyNameReference.Parse("System.ServiceModel.Primitives");
            ServiceModelHTTPRef = AssemblyNameReference.Parse("System.ServiceModel.Http");
            JsonSerializationRef = AssemblyNameReference.Parse("System.Runtime.Serialization.Json");
            ObjectModelRef = AssemblyNameReference.Parse("System.ObjectModel");

            // (There is no longer a dedicated GamerServices assembly ref. Version 19 dissolved
            //  Microsoft.Xna.Framework.GamerServices into WPR.Framework.Xna, so the whole surface
            //  now rides WprFrameworkXnaRef — except GamerServicesComponent, which derives from
            //  FNA's GameComponent and is rescoped to FnaBackendRef.)


            // *** Patches ***
            Patches = new Dictionary<string, TypePatchInfo>()
            {
                { "Microsoft.Xna.Framework.GraphicsDeviceManager", new TypePatchInfo()
                {
                    NewName = "GraphicsDeviceManager",
                    NewNamespace = "WPR.Backend.FNA.Compat",
                    Reference = FnaBackendRef
                }
                },
                // WP8's mscorlib carries the property-changing family; .NET's mscorlib facade does
                // not forward it (it was System.dll on the desktop), and neither does System.Runtime:
                // it lives in System.ObjectModel. MvvmLight
                // WP8's ObservableObject implements INotifyPropertyChanging, so every view model in
                // Microsoft Bingo failed to load ("Could not load type ... from assembly mscorlib").
                { "System.ComponentModel.INotifyPropertyChanging", new TypePatchInfo()
                {
                    Reference = ObjectModelRef
                }
                },
                { "System.ComponentModel.PropertyChangingEventHandler", new TypePatchInfo()
                {
                    Reference = ObjectModelRef
                }
                },
                { "System.ComponentModel.PropertyChangingEventArgs", new TypePatchInfo()
                {
                    Reference = ObjectModelRef
                }
                },
                // NOTE: the WP control/shell types that used to be redirected here
                // (Microsoft.Phone.Controls.GestureService / GestureListener / *GestureEventArgs,
                // PhoneApplicationFrame, PhoneApplicationPage, and every Microsoft.Phone.Shell.*
                // lifecycle type) now live in the Microsoft.Phone assembly under their real
                // namespaces, so user IL binds them natively — no patch entry needed.

                { "System.ServiceModel.XmlSerializerFormatAttribute", new TypePatchInfo()
                {
                    Reference = ServiceModelPrimitivesRef
                }
                },
                { "System.ServiceModel.BasicHttpBinding", new TypePatchInfo()
                {
                    Reference = ServiceModelHTTPRef
                }
                },
                { "System.ServiceModel.BasicHttpSecurity", new TypePatchInfo()
                {
                    Reference = ServiceModelHTTPRef
                }
                },
                { "System.ServiceModel.BasicHttpSecurityMode", new TypePatchInfo()
                {
                    Reference = ServiceModelHTTPRef
                }
                },
                // Version 47: the HTTP channel types live in System.ServiceModel.Http too, not
                // Primitives (which the System.ServiceModel scope is rewritten to). Arkadium's
                // NetworkManager builds its own binding from them in its Xbox service setup, so
                // Microsoft Mahjong / Solitaire / Minesweeper sat on their splash for ever.
                { "System.ServiceModel.Channels.HttpTransportBindingElement", new TypePatchInfo() { Reference = ServiceModelHTTPRef } },
                { "System.ServiceModel.Channels.HttpsTransportBindingElement", new TypePatchInfo() { Reference = ServiceModelHTTPRef } },
                { "System.ServiceModel.Channels.IHttpCookieContainerManager", new TypePatchInfo() { Reference = ServiceModelHTTPRef } },
                { "System.ServiceModel.Channels.HttpRequestMessageProperty", new TypePatchInfo() { Reference = ServiceModelHTTPRef } },
                { "System.ServiceModel.Channels.HttpResponseMessageProperty", new TypePatchInfo() { Reference = ServiceModelHTTPRef } },
                { "System.ServiceModel.HttpTransportSecurity", new TypePatchInfo() { Reference = ServiceModelHTTPRef } },
                { "System.ServiceModel.HttpClientCredentialType", new TypePatchInfo() { Reference = ServiceModelHTTPRef } },
                // WP8 compiled it against System.ServiceModel.Web, whose .NET facade predates it.
                { "System.Runtime.Serialization.Json.DataContractJsonSerializerSettings", new TypePatchInfo() { Reference = JsonSerializationRef } },
                // Silverlight 4 / WP7.1 local-network multiplayer. .NET has no such type, so the
                // typeref (scoped to WP's System.Net) resolved to nothing: PAC-MAN Kart Rally's
                // "Host game" threw TypeLoadException from its channel constructor. The shim keeps
                // Silverlight's namespace; only the assembly (WP's System.Net) changes.
                { "System.Net.Sockets.UdpAnySourceMulticastClient", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef
                }
                },
                //!
                { "System.Security.Cryptography.ProtectedData", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    //RnD : if uncomment it, WPR.WindowsCompabilityProtectedData class will be used
                    NewName = "ProtectedData",
                    NewNamespace = "WPR.WindowsCompability"
                }
                },
            };

            // *** Member Patches ***
            MemberPatches = new Dictionary<string, Type>
            {
                // Every C# `lock`. See WPR.Xna.Compat.Monitor and patcher v40.
                {
                    "System.Void System.Threading.Monitor::Enter(System.Object)",
                    typeof(WPR.Xna.Compat.Monitor)
                },
                {
                    "System.Void System.Threading.Monitor::Enter(System.Object,System.Boolean&)",
                    typeof(WPR.Xna.Compat.Monitor)
                },

                // RnD ***************************************
                //{
                //    "Microsoft.Xna.Framework.GamerServices.LeaderboardReader Microsoft.Xna.Framework.GamerServices.LeaderboardReader::Read(Microsoft.Xna.Framework.GamerServices.LeaderboardIdentity, Microsoft.Xna.Framework.GamerServices.Gamer, Int32)",
                //    typeof(Microsoft.Xna.Framework.GamerServices2.LeaderboardReader)
                //},
                // *******************************************

                // Open IsolatedStorage file streams with FileShare.ReadWrite. WP7 was single-
                // process so games never needed to share; under WPR (one process, collectible
                // ALCs) a static stream left open by a prior launch — or a second thread racing
                // an unsynchronised open — otherwise throws "being used by another process"
                // (e.g. Battleship's Profiler debug.log). Redirects the two ctors games use to a
                // subclass that adds the share flag; same store, same access otherwise.
                {
                    "System.Void System.IO.IsolatedStorage.IsolatedStorageFileStream::.ctor(System.String,System.IO.FileMode,System.IO.IsolatedStorage.IsolatedStorageFile)",
                    typeof(WPR.WindowsCompability.SharedIsolatedStorageFileStream)
                },
                {
                    "System.Void System.IO.IsolatedStorage.IsolatedStorageFileStream::.ctor(System.String,System.IO.FileMode,System.IO.FileAccess,System.IO.IsolatedStorage.IsolatedStorageFile)",
                    typeof(WPR.WindowsCompability.SharedIsolatedStorageFileStream)
                },

                {
                    "System.Byte[] System.Security.Cryptography.ProtectedData::Protect(System.Byte[],System.Byte[])",
                    typeof(WPR.WindowsCompability.ProtectedData)
                },

                {
                    "System.Byte[] System.Security.Cryptography.ProtectedData::Unprotect(System.Byte[],System.Byte[])",
                    typeof(WPR.WindowsCompability.ProtectedData)
                },

                // ---- Windows path separators in game file I/O -------------------------------
                //
                // WP7 titles were built on Windows, where '\' and '/' are interchangeable, so
                // hardcoded paths like "Content\Credits.xml" are everywhere — 7 of the 26
                // installed titles carry one, and one carries 89. On Android '\' is an ordinary
                // filename character, so the open fails, the game swallows it, and the symptom
                // surfaces somewhere unrelated. Battlewagon is the reference case: one failed
                // XmlReader.Create left a null field and TitleScene.Update then threw an NRE on
                // every frame (5,593 in one run) — its menu never built while the background
                // animated happily. The rules are declared by the platform; see
                // WPR.Engine.Content.ContentPaths.
                //
                // These normalise at the point of USE rather than rewriting string literals, so
                // a path assembled at run time (concatenation, Path.Combine, "Level{0}\{0}.txt")
                // is covered too. Windows behaviour is unchanged — the normaliser is a no-op
                // when the platform separator is already '\'.
                //
                // NOT covered: System.IO.FileInfo / DirectoryInfo. Both are SEALED, so no
                // subclass can stand where the constructed instance lands, and retargeting the
                // declaring type is all this table can do. They need a call-site rewrite like
                // RedirectIsolatedStorageCalls; only 3 uses exist across the installed library,
                // so that is deliberately left until something actually needs it.
                {
                    "System.IO.FileStream System.IO.File::Open(System.String,System.IO.FileMode)",
                    typeof(WPR.WindowsCompability.File2)
                },
                {
                    "System.IO.FileStream System.IO.File::Open(System.String,System.IO.FileMode,System.IO.FileAccess)",
                    typeof(WPR.WindowsCompability.File2)
                },
                {
                    "System.IO.FileStream System.IO.File::Open(System.String,System.IO.FileMode,System.IO.FileAccess,System.IO.FileShare)",
                    typeof(WPR.WindowsCompability.File2)
                },
                {
                    "System.Boolean System.IO.File::Exists(System.String)",
                    typeof(WPR.WindowsCompability.File2)
                },
                {
                    "System.IO.StreamReader System.IO.File::OpenText(System.String)",
                    typeof(WPR.WindowsCompability.File2)
                },
                {
                    "System.IO.FileStream System.IO.File::OpenRead(System.String)",
                    typeof(WPR.WindowsCompability.File2)
                },
                {
                    "System.IO.FileStream System.IO.File::OpenWrite(System.String)",
                    typeof(WPR.WindowsCompability.File2)
                },
                {
                    "System.IO.FileStream System.IO.File::Create(System.String)",
                    typeof(WPR.WindowsCompability.File2)
                },
                {
                    "System.IO.StreamWriter System.IO.File::CreateText(System.String)",
                    typeof(WPR.WindowsCompability.File2)
                },
                {
                    "System.IO.StreamWriter System.IO.File::AppendText(System.String)",
                    typeof(WPR.WindowsCompability.File2)
                },
                {
                    "System.String System.IO.File::ReadAllText(System.String)",
                    typeof(WPR.WindowsCompability.File2)
                },
                {
                    "System.Byte[] System.IO.File::ReadAllBytes(System.String)",
                    typeof(WPR.WindowsCompability.File2)
                },
                {
                    "System.String[] System.IO.File::ReadAllLines(System.String)",
                    typeof(WPR.WindowsCompability.File2)
                },
                {
                    "System.Void System.IO.File::WriteAllText(System.String,System.String)",
                    typeof(WPR.WindowsCompability.File2)
                },
                {
                    "System.Void System.IO.File::WriteAllBytes(System.String,System.Byte[])",
                    typeof(WPR.WindowsCompability.File2)
                },
                {
                    "System.Void System.IO.File::AppendAllText(System.String,System.String)",
                    typeof(WPR.WindowsCompability.File2)
                },
                {
                    "System.Void System.IO.File::Delete(System.String)",
                    typeof(WPR.WindowsCompability.File2)
                },
                {
                    "System.Void System.IO.File::Copy(System.String,System.String)",
                    typeof(WPR.WindowsCompability.File2)
                },
                {
                    "System.Void System.IO.File::Copy(System.String,System.String,System.Boolean)",
                    typeof(WPR.WindowsCompability.File2)
                },
                {
                    "System.Void System.IO.File::Move(System.String,System.String)",
                    typeof(WPR.WindowsCompability.File2)
                },

                {
                    "System.Boolean System.IO.Directory::Exists(System.String)",
                    typeof(WPR.WindowsCompability.Directory2)
                },
                {
                    "System.IO.DirectoryInfo System.IO.Directory::CreateDirectory(System.String)",
                    typeof(WPR.WindowsCompability.Directory2)
                },
                {
                    "System.Void System.IO.Directory::Delete(System.String)",
                    typeof(WPR.WindowsCompability.Directory2)
                },
                {
                    "System.Void System.IO.Directory::Delete(System.String,System.Boolean)",
                    typeof(WPR.WindowsCompability.Directory2)
                },
                {
                    "System.String[] System.IO.Directory::GetFiles(System.String)",
                    typeof(WPR.WindowsCompability.Directory2)
                },
                {
                    "System.String[] System.IO.Directory::GetFiles(System.String,System.String)",
                    typeof(WPR.WindowsCompability.Directory2)
                },
                {
                    "System.String[] System.IO.Directory::GetDirectories(System.String)",
                    typeof(WPR.WindowsCompability.Directory2)
                },
                {
                    "System.String[] System.IO.Directory::GetDirectories(System.String,System.String)",
                    typeof(WPR.WindowsCompability.Directory2)
                },

                // Constructors: the replacement must be SUBSTITUTABLE for the original, because
                // all this table does is retarget the newobj's declaring type. FileStream,
                // StreamReader and StreamWriter are all unsealed, so a subclass works — the same
                // mechanism SharedIsolatedStorageFileStream already uses.
                {
                    "System.Void System.IO.FileStream::.ctor(System.String,System.IO.FileMode)",
                    typeof(WPR.WindowsCompability.NormalizedPathFileStream)
                },
                {
                    "System.Void System.IO.FileStream::.ctor(System.String,System.IO.FileMode,System.IO.FileAccess)",
                    typeof(WPR.WindowsCompability.NormalizedPathFileStream)
                },
                {
                    "System.Void System.IO.FileStream::.ctor(System.String,System.IO.FileMode,System.IO.FileAccess,System.IO.FileShare)",
                    typeof(WPR.WindowsCompability.NormalizedPathFileStream)
                },
                {
                    "System.Void System.IO.StreamReader::.ctor(System.String)",
                    typeof(WPR.WindowsCompability.NormalizedPathStreamReader)
                },
                {
                    "System.Void System.IO.StreamReader::.ctor(System.String,System.Text.Encoding)",
                    typeof(WPR.WindowsCompability.NormalizedPathStreamReader)
                },
                {
                    "System.Void System.IO.StreamReader::.ctor(System.String,System.Boolean)",
                    typeof(WPR.WindowsCompability.NormalizedPathStreamReader)
                },
                {
                    "System.Void System.IO.StreamWriter::.ctor(System.String)",
                    typeof(WPR.WindowsCompability.NormalizedPathStreamWriter)
                },
                {
                    "System.Void System.IO.StreamWriter::.ctor(System.String,System.Boolean)",
                    typeof(WPR.WindowsCompability.NormalizedPathStreamWriter)
                },
                {
                    "System.Void System.IO.StreamWriter::.ctor(System.String,System.Boolean,System.Text.Encoding)",
                    typeof(WPR.WindowsCompability.NormalizedPathStreamWriter)
                },

                // XmlReader.Create is the one that actually broke Battlewagon: it resolves its
                // argument as a URI and opens the FileStream inside XmlDownloadManager, so the
                // System.IO entries above never see the path.
                {
                    "System.Xml.XmlReader System.Xml.XmlReader::Create(System.String)",
                    typeof(WPR.WindowsCompability.XmlReader2)
                },
                {
                    "System.Xml.XmlReader System.Xml.XmlReader::Create(System.String,System.Xml.XmlReaderSettings)",
                    typeof(WPR.WindowsCompability.XmlReader2)
                },
                {
                    "System.Xml.Linq.XDocument System.Xml.Linq.XDocument::Load(System.String)",
                    typeof(WPR.WindowsCompability.XDocument2)
                },
                {
                    "System.Xml.Linq.XDocument System.Xml.Linq.XDocument::Load(System.String,System.Xml.Linq.LoadOptions)",
                    typeof(WPR.WindowsCompability.XDocument2)
                },
                // NOTE: XElement::Load(String) is NOT listed here — it already had an entry at the
                // bottom of this table, pointing at the same XElement2. That shim predates this
                // block and does the same normalisation plus an install-folder fallback for
                // relative paths; only the LoadOptions overload was missing.
                {
                    "System.Xml.Linq.XElement System.Xml.Linq.XElement::Load(System.String,System.Xml.Linq.LoadOptions)",
                    typeof(WPR.WindowsCompability.XElement2)
                },
                // ---- end Windows path separators --------------------------------------------

                //{
                //    "System.Windows.Media.Imaging.WriteableBitmap System.Windows.Media.Imaging.WriteableBitmap(System.Integer,System.Integer)",
                //    typeof(System.Windows.Media.Imaging.WriteableBitmap)
                //},
                //{
                //    "System.Void System.Windows.Media.Imaging.BitmapSource::SetSource()",
                //    typeof(System.Windows.Media.Imaging.BitmapSource)
                //},

                {
                    "System.Type System.Type::GetType(System.String,System.Boolean)",
                    typeof(WPR.WindowsCompability.Type2)
                },
                {
                    "System.Type System.Type::GetType(System.String)",
                    typeof(WPR.WindowsCompability.Type2)
                },
                {
                    // A library asking for the entry assembly by name (the ad SDK's
                    // AdManager..cctor) cannot bind into the game's collectible load context.
                    "System.Reflection.Assembly System.Reflection.Assembly::Load(System.String)",
                    typeof(WPR.WindowsCompability.Assembly2)
                },
                {
                    "Microsoft.Xna.Framework.Graphics.DisplayMode Microsoft.Xna.Framework.Graphics.GraphicsDevice::get_DisplayMode()",
                    typeof(WPR.Xna.Compat.GraphicsDevice)
                },
                {
                    "Microsoft.Xna.Framework.Graphics.DisplayMode Microsoft.Xna.Framework.Graphics.GraphicsAdapter::get_CurrentDisplayMode()",
                    typeof(WPR.Xna.Compat.GraphicsAdapter)
                },

                {
                    "System.String System.IO.Path::GetDirectoryName(System.String)",
                    typeof(WPR.WindowsCompability.Path2)
                },
                {
                    "System.String System.IO.Path::GetFileName(System.String)",
                    typeof(WPR.WindowsCompability.Path2)
                },
                {
                    "System.String System.IO.Path::GetFileNameWithoutExtension(System.String)",
                    typeof(WPR.WindowsCompability.Path2)
                },
                {
                    "System.Void System.GC::Collect()",
                    typeof(WPR.WindowsCompability.GC2)
                },

                {
                    "System.Xml.Linq.XElement System.Xml.Linq.XElement::Load(System.String)",
                    typeof(WPR.WindowsCompability.XElement2)
                },

            };

        }//ApplicationPatcher

        /// <summary>
        /// Rescopes every <c>typeof(...)</c> argument in every custom attribute in the module.
        /// </summary>
        /// <remarks>
        /// <para><b>A custom attribute blob names types by STRING, so the TypeRef rescope cannot
        /// see them.</b> An argument of type <c>System.Type</c> is stored as an assembly-qualified
        /// name — <c>"Microsoft.Xna.Framework.Vector2, Microsoft.Xna.Framework, Version=4.0.0.0,
        /// …, PublicKeyToken=842cf8be1de50553"</c> — not as a row in the TypeRef table, so
        /// <c>module.GetTypeReferences()</c> never returns it and every redirect this patcher
        /// performs used to pass it by. The name then points at a WP7 assembly that does not
        /// exist at runtime.</para>
        ///
        /// <para><b>Reading the arguments is what makes the fix stick, and it is also why the bug
        /// existed.</b> Cecil parses a blob lazily and, if nothing ever touches
        /// <c>ConstructorArguments</c>/<c>Properties</c>/<c>Fields</c>, writes the original bytes
        /// back verbatim — which is exactly what happened before this method: the patcher rewrote
        /// the assembly ref from <c>Microsoft.Xna.Framework</c> to <c>FNA</c> and the blob kept
        /// saying <c>Microsoft.Xna.Framework</c> regardless. Touching them here forces Cecil to
        /// materialise each argument into a <see cref="TypeReference"/> and to re-serialise the
        /// blob from that model on write, so mutating the reference is enough.</para>
        ///
        /// <para><b>The failure is a hang, not a crash</b>, because the types that carry
        /// <c>typeof()</c> in practice are <c>XmlSerializer</c> hints — <c>[XmlElement]</c>,
        /// <c>[XmlArrayItem]</c>, <c>[XmlInclude]</c>. Nothing loads the type until a serializer is
        /// constructed over the declaring type, and then the <c>TypeLoadException</c> arrives
        /// wrapped in <c>InvalidOperationException: There was an error reflecting type '…'</c>,
        /// which games routinely swallow. Fight Game Rivals
        /// (<c>{57b854f3-a3cc-4213-aa91-07aae56e146c}</c>) is the reference case: one
        /// <c>[XmlArrayItem(ElementName = "Vector2", Type = typeof(Vector2))]</c> on
        /// <c>GameObjectManager.BaseGameObject.CustomData.xmlValues</c> failed the serializer for
        /// <c>Manager.xmlGameObjectSpecification</c>, which is how EVERY screen in the game is
        /// deserialised — so it sat on its splash screen for ever, with the only trace a
        /// first-chance exception in the per-game log.</para>
        /// </remarks>
        private static void RescopeCustomAttributeTypeArguments(
            ModuleDefinition module,
            Action<TypeReference> rescope)
        {
            /* Attribute types whose blob could not be parsed at all, deduplicated. Reported as one
             * line per assembly rather than one per attribute: the usual cause is a BCL attribute
             * (DebuggableAttribute, EditorBrowsableAttribute) whose enum argument lives in WP7's
             * own mscorlib/System, which nothing on this machine can resolve — hundreds of
             * identical lines per install, none of them actionable. Still reported, because a
             * GAME attribute in this list is the one case where a typeof() silently keeps its
             * dead WP7 assembly name. */
            SortedSet<string> unparsedAttributeTypes = new SortedSet<string>(StringComparer.Ordinal);

            RescopeProvider(module.Assembly);
            RescopeProvider(module);

            foreach (TypeDefinition type in module.GetTypes())
            {
                RescopeProvider(type);

                foreach (FieldDefinition field in type.Fields)
                {
                    RescopeProvider(field);
                }

                foreach (PropertyDefinition property in type.Properties)
                {
                    RescopeProvider(property);
                }

                foreach (EventDefinition evt in type.Events)
                {
                    RescopeProvider(evt);
                }

                foreach (MethodDefinition method in type.Methods)
                {
                    RescopeProvider(method);
                    RescopeProvider(method.MethodReturnType);

                    foreach (ParameterDefinition parameter in method.Parameters)
                    {
                        RescopeProvider(parameter);
                    }
                }
            }

            ReportUnparsed();

            void RescopeProvider(ICustomAttributeProvider? provider)
            {
                if (provider == null || !provider.HasCustomAttributes)
                {
                    return;
                }

                foreach (CustomAttribute attribute in provider.CustomAttributes)
                {
                    /* An attribute whose own type cannot be resolved throws from the blob parser
                     * (it needs the constructor's signature to know each argument's type). That is
                     * not fatal on its own — an unresolvable attribute is inert unless something
                     * reflects over it — so skip it and leave its bytes untouched rather than
                     * failing the whole install. */
                    try
                    {
                        foreach (CustomAttributeArgument argument in attribute.ConstructorArguments)
                        {
                            RescopeArgument(argument);
                        }

                        foreach (CustomAttributeNamedArgument named in attribute.Properties)
                        {
                            RescopeArgument(named.Argument);
                        }

                        foreach (CustomAttributeNamedArgument named in attribute.Fields)
                        {
                            RescopeArgument(named.Argument);
                        }
                    }
                    catch (Exception)
                    {
                        unparsedAttributeTypes.Add(attribute.AttributeType.FullName);
                    }
                }
            }

            void RescopeArgument(CustomAttributeArgument argument)
            {
                switch (argument.Value)
                {
                    // typeof(...). Mutating the reference in place is what the writer picks up —
                    // CustomAttributeArgument is a struct, so assigning a new one to the local
                    // would be thrown away.
                    case TypeReference typeArgument:
                        RescopeTypeTree(typeArgument);
                        break;

                    // An array-valued argument, e.g. Type[].
                    case CustomAttributeArgument[] arrayArgument:
                        foreach (CustomAttributeArgument element in arrayArgument)
                        {
                            RescopeArgument(element);
                        }
                        break;

                    // A boxed argument — what an `object`-typed attribute parameter holds.
                    case CustomAttributeArgument boxedArgument:
                        RescopeArgument(boxedArgument);
                        break;
                }
            }

            void RescopeTypeTree(TypeReference type)
            {
                /* typeof(List<Vector2>) and typeof(Vector2[]) both hide the interesting reference
                 * one level down, and only the leaf carries a scope worth rewriting. */
                if (type is GenericInstanceType genericInstance)
                {
                    foreach (TypeReference genericArgument in genericInstance.GenericArguments)
                    {
                        RescopeTypeTree(genericArgument);
                    }
                }

                if (type is TypeSpecification specification)
                {
                    RescopeTypeTree(specification.ElementType);
                    return;
                }

                if (type.IsGenericParameter)
                {
                    return;
                }

                rescope(type);
            }

            void ReportUnparsed()
            {
                if (unparsedAttributeTypes.Count == 0)
                {
                    return;
                }

                Log.Warn(LogCategory.AppInstall,
                    "[attr-fixup] " + module.Name + ": could not parse "
                    + unparsedAttributeTypes.Count + " attribute type(s), so any typeof() in them "
                    + "keeps its original assembly name: "
                    + string.Join(", ", unparsedAttributeTypes));
            }
        }

        private void PatchRelaxedXmlNullableAttribTextSerialize(ModuleDefinition? module)
        {
            Queue<TypeDefinition> typeScanQueue = new Queue<TypeDefinition>();
            foreach (var typeDef in module!.Types)
            {
                typeScanQueue.Enqueue(typeDef);
            }

            CustomAttribute? xmlIgnoreAttrib = null;

            // Patch type for resolve XML library incompability
            while (typeScanQueue.Count != 0)
            {
                TypeDefinition type = typeScanQueue.Dequeue();

                if (type.HasNestedTypes)
                {
                    foreach (var typeNested in type.NestedTypes)
                    {
                        typeScanQueue.Enqueue(typeNested);
                    }
                }

                foreach (var field in type.Fields)
                {
                    CustomAttribute? xmlNonNullableProp = null;

                    foreach (var attrib in field.CustomAttributes)
                    {
                        if (attrib.AttributeType.FullName == typeof(XmlAttributeAttribute).FullName)
                        {
                            xmlNonNullableProp = attrib;
                            break;
                        }
                    }

                    if (xmlNonNullableProp == null)
                    {
                        continue;
                    }

                    if (field.FieldType.FullName.Contains("System.Nullable"))
                    {
                        var actualFieldType = (field.FieldType as GenericInstanceType)!.GenericArguments[0];

                        // Generate holder getter/setter
                        var getterMethod = new MethodDefinition($"get_{field.Name}SerializableHolder",
                            MethodAttributes.Public, actualFieldType);

                        var getterGen = getterMethod.Body.GetILProcessor();

                        var nullableRefTypeGeneric = module.ImportReference(
                            Type.GetType("System.Nullable`1")!);

                        var nullableRefType =
                            nullableRefTypeGeneric.MakeGenericInstanceType(new TypeReference[]
                            { actualFieldType });

                        // Emit getter
                        getterGen.Emit(OpCodes.Ldarg_0);
                        getterGen.Emit(OpCodes.Ldflda, field);
                        getterGen.Emit(OpCodes.Call, new MethodReference("get_Value",
                            nullableRefTypeGeneric.GenericParameters[0])
                        {
                            HasThis = true,
                            DeclaringType = nullableRefType
                        });

                        getterGen.Emit(OpCodes.Ret);

                        // Emit setter
                        var setterMethod = new MethodDefinition($"set_{field.Name}SerializableHolder",
                            MethodAttributes.Public, module.TypeSystem.Void)
                        {
                            Parameters = { new ParameterDefinition(actualFieldType) },
                            HasThis = true
                        };
                        var setterGen = setterMethod.Body.GetILProcessor();

                        setterGen.Emit(OpCodes.Ldarg_0);
                        setterGen.Emit(OpCodes.Ldarg_1);
                        setterGen.Emit(OpCodes.Newobj, new MethodReference(".ctor",
                            module.TypeSystem.Void, nullableRefType)
                        {
                            Parameters = { new ParameterDefinition(
                                nullableRefTypeGeneric.GenericParameters[0]) },
                            HasThis = true
                        });

                        setterGen.Emit(OpCodes.Stfld, field);
                        setterGen.Emit(OpCodes.Ret);

                        // Emit skip serialize consideration
                        var shouldSerializeMethod = new MethodDefinition(
                            $"ShouldSerialize{field.Name}SerializableHolder",
                            MethodAttributes.Public, module.TypeSystem.Boolean);

                        var shouldSerializeGen = shouldSerializeMethod.Body.GetILProcessor();

                        shouldSerializeGen.Emit(OpCodes.Ldarg_0);
                        shouldSerializeGen.Emit(OpCodes.Ldflda, field);
                        shouldSerializeGen.Emit(OpCodes.Call, new MethodReference(
                            "HasValue", module.TypeSystem.Boolean, nullableRefType)
                        {
                            HasThis = true
                        });
                        shouldSerializeGen.Emit(OpCodes.Ret);

                        type.Methods.Add(shouldSerializeMethod);
                        type.Methods.Add(getterMethod);
                        type.Methods.Add(setterMethod);

                        var propSeri = new PropertyDefinition(
                            $"{field.Name}SerializableHolder", PropertyAttributes.None, actualFieldType)
                        {
                            GetMethod = getterMethod,
                            SetMethod = setterMethod
                        };

                        type.Properties.Add(propSeri);

                        if (xmlIgnoreAttrib == null)
                        {
                            xmlIgnoreAttrib = new CustomAttribute(module.ImportReference(typeof(XmlIgnoreAttribute).
                                GetConstructor(Type.EmptyTypes)));
                        }

                        field.CustomAttributes.Remove(xmlNonNullableProp);
                        field.CustomAttributes.Add(xmlIgnoreAttrib);

                        // Add attribute if they already gave name, else we need to be creative
                        if (xmlNonNullableProp.HasConstructorArguments)
                        {
                            propSeri.CustomAttributes.Add(xmlNonNullableProp);
                        }
                        else
                        {
                            var attributeType = (xmlNonNullableProp.AttributeType.FullName
                                == typeof(XmlAttributeAttribute).FullName)
                                    ? typeof(XmlAttributeAttribute)
                                    : typeof(XmlTextAttribute);

                            MethodReference methodConstructor = module.ImportReference(attributeType
                                .GetConstructor(new Type[] { typeof(String) }));

                            propSeri.CustomAttributes.Add(new CustomAttribute(methodConstructor)
                            {
                                ConstructorArguments = {
                                    new CustomAttributeArgument(module.TypeSystem.String, field.Name) }
                            });
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Rewrites every <c>store.OpenFile(…)</c> / <c>store.CreateFile(…)</c> call in the game to
        /// the matching static on <see cref="WPR.WindowsCompability.SharedIsolatedStorage"/>, which
        /// opens with <see cref="FileShare.ReadWrite"/> instead of the BCL's
        /// <see cref="FileShare"/>.None.
        ///
        /// <para><b>Why an IL rewrite and not a table entry.</b> <see cref="MemberPatches"/> only
        /// swaps a member reference's <c>DeclaringType</c>, which needs the replacement type to be
        /// substitutable for the instance already on the stack — and
        /// <c>System.IO.IsolatedStorage.IsolatedStorageFile</c> is <c>sealed</c>, so nothing can
        /// stand in for it. Turning <c>callvirt instance T Store::OpenFile(a, b)</c> into
        /// <c>call T Shim::OpenFile(Store, a, b)</c> leaves the evaluation stack byte-for-byte
        /// identical (the instance simply becomes argument zero), so no other IL has to move.</para>
        ///
        /// <para><b>Why it is needed on top of <c>SharedIsolatedStorageFileStream</c>.</b> That
        /// stream shim is installed through <see cref="MemberPatches"/> for the two
        /// <c>IsolatedStorageFileStream</c> constructors, and covers games that <c>new</c> a stream
        /// themselves. <c>OpenFile</c> constructs its stream <i>inside the BCL</i>, which the
        /// patcher can never reach — so games that open through the store got none of the fix.
        /// Angry Birds is the reference case: a leaked read handle blocks its own later write, the
        /// game swallows the failure and writes to a null stream, and it silently never saves. See
        /// <see cref="WPR.WindowsCompability.SharedIsolatedStorage"/> for the full chain.</para>
        ///
        /// <para>Runs over every game assembly, not a named title: leaking an isolated-storage
        /// handle was free on a WP7 device and costly only under WPR's one-process model, so the
        /// same latent bug is expected across the library.</para>
        /// </summary>
        /// <summary>
        /// Works around a defect in MonoVM's IL importer, which is what net8.0-android runs on.
        ///
        /// Mono carries the evaluation-stack state linearly into the instruction FOLLOWING an
        /// unconditional branch. When that instruction happens to begin a block entered from
        /// somewhere else at a different depth, the two states conflict and Mono refuses to
        /// compile the WHOLE METHOD, throwing InvalidProgramException with an EMPTY message.
        /// CoreCLR does not do this and ILVerify does not flag it, because the block is perfectly
        /// legal: its real predecessors all enter it at the depth it expects, and control never
        /// falls through the branch into it.
        ///
        /// Proven by an A/B on device: an identical three-instruction block reachable only through
        /// a switch FAILS when it is sited after a br that left depth 2, and COMPILES unchanged
        /// when sited after a br that left depth 0.
        ///
        /// Obfuscators produce this shape constantly. Control-flow flattening emits one giant
        /// switch over a state local with every arm ending in "stloc state; br dispatch", so arms
        /// sit back to back and an arm that leaves the stack non-empty is immediately followed by
        /// the next arm's entry point. Brain Challenge HD is the reference case: two methods
        /// failed this way, which killed Game.Update on every frame and left the game rendering a
        /// screen that never advanced and never took input.
        ///
        /// The fix is to COPY the affected block to the end of the method and repoint its
        /// entrants at the copy. The original stays put and simply becomes unreachable, and Mono
        /// does not import unreachable code -- which is precisely why this cures it.
        ///
        /// COPY, NEVER MOVE. Moving the block makes whatever followed it the new neighbour of the
        /// same br, which re-creates the defect somewhere else: a move-based version of this
        /// fixed the two known failures and introduced 2,816 new ones in a single method.
        ///
        /// Three restrictions keep the transform safe, and each one was a real failure first:
        ///  * methods with protected regions are skipped, or the appended clone would sit outside
        ///    the try/handler it came from (ILVerify: BranchOutOfFinally);
        ///  * the method must already END in ret/throw, or the clone becomes reachable by
        ///    fallthrough at the wrong depth (ILVerify: PathStackDepth);
        ///  * a block must contain no other entry point, or repointing its entrants would strand
        ///    whoever jumped into the middle of it.
        ///
        /// A block may end in ret, throw or br, and the terminator decides what the clone hands
        /// to the NEXT appended clone -- the very defect this pass removes, recreated among the
        /// clones themselves. ret and throw end the flow, so whatever follows starts clean; that
        /// is why the original method must end in one of them, and why the first clone is always
        /// safe. A br does not: it hands its own depth straight on.
        ///
        /// So the clones are ORDERED rather than merely filtered. Every ret/throw clone goes
        /// first, in any order, because each one resets the carry for its successor. The
        /// br-terminated clones then follow as a chain in which each one's exit depth equals the
        /// next one's entry depth; the first of them is free because a reset precedes it. A block
        /// that cannot be chained is simply left alone.
        ///
        /// br-terminated blocks were excluded outright until 2026-09-19, and that left whole
        /// methods unfixed: Earthworm Jim's b2PolygonShape::.ctor has four conflicting blocks and
        /// every one of them ends in br, so the method stayed broken and took b2Shape::Create --
        /// its only caller -- down with it on every level load.
        ///
        /// Windows is unaffected either way (CoreCLR compiles the original shape fine), so this
        /// runs unconditionally rather than per-platform: one install is shared between the heads,
        /// and a game patched on a PC and copied to a phone has to carry the fix.
        /// </summary>
        /// <summary>
        /// Diagnostic kill switch for <see cref="RelocateMonoStackConflictBlocks"/>: set the
        /// environment variable <c>WPR_PATCHER_DISABLE_MONO_RELOCATION</c> to any non-empty value
        /// and the pass is skipped with everything else unchanged. Exists so an A/B of one game's
        /// IL with and without the pass is a repatch rather than a rebuild.
        /// </summary>
        public static bool MonoRelocationDisabled =>
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WPR_PATCHER_DISABLE_MONO_RELOCATION"));

        /// <summary>
        /// The v28 half of the pass — relocating blocks that end in <c>br</c> as a depth-matched
        /// chain — is opt-in: set <c>WPR_PATCHER_MONO_RELOCATION_BR</c> to any non-empty value.
        /// Off, br-terminated blocks are left in place and only ret/throw-terminated ones move
        /// (the v27 behaviour). It is off because the br clones are what stopped Earthworm Jim
        /// and Final Fantasy III starting under the Mono JIT (Release APK) — see the v29 note on
        /// <see cref="Version"/>. The code is kept, behind this switch, so the A/B stays a
        /// repatch rather than an archaeology dig.
        /// </summary>
        public static bool MonoRelocationBrEnabled =>
            !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WPR_PATCHER_MONO_RELOCATION_BR"));

        private static void RelocateMonoStackConflictBlocks(ModuleDefinition module, string moduleName)
        {
            int relocated = 0;
            int methodsTouched = 0;

            foreach (TypeDefinition type in module.GetTypes())
            {
                foreach (MethodDefinition method in type.Methods)
                {
                    if (!method.HasBody || method.Body.Instructions.Count == 0)
                    {
                        continue;
                    }

                    MethodBody body = method.Body;

                    if (body.ExceptionHandlers.Count > 0)
                    {
                        continue;
                    }

                    List<Instruction> instructions = new List<Instruction>(body.Instructions);
                    Instruction last = instructions[instructions.Count - 1];
                    if (last.OpCode.Code != Code.Ret && last.OpCode.Code != Code.Throw)
                    {
                        continue;
                    }

                    int[] depth = ComputeEntryStackDepths(instructions);

                    HashSet<Instruction> targets = new HashSet<Instruction>();
                    foreach (Instruction ins in instructions)
                    {
                        if (ins.Operand is Instruction single)
                        {
                            targets.Add(single);
                        }
                        else if (ins.Operand is Instruction[] many)
                        {
                            foreach (Instruction t in many)
                            {
                                targets.Add(t);
                            }
                        }
                    }

                    // Collected up front against the ORIGINAL layout. Copying only appends, so
                    // these indices stay valid for the whole pass.
                    List<RelocatableBlock> candidates = new List<RelocatableBlock>();
                    for (int i = 1; i < instructions.Count; i++)
                    {
                        Instruction previous = instructions[i - 1];
                        if (previous.OpCode.Code != Code.Br && previous.OpCode.Code != Code.Br_S)
                        {
                            continue;
                        }

                        // Only a block someone BRANCHES to can disagree with the state the branch
                        // left behind; straight-line code after a br is simply unreachable.
                        if (!targets.Contains(instructions[i]))
                        {
                            continue;
                        }

                        // A br that left the stack empty agrees with every entry point.
                        //
                        // Widening this to "carried depth differs from entry depth, either way"
                        // was tried on 2026-09-19 and reverted: it flags 25,918 sites across the
                        // installed library, including all through Angry Birds, Pac-Man and
                        // Guitar Hero 5, which run on Android today. A predicate that fires on
                        // tens of thousands of shapes MonoVM demonstrably accepts is not
                        // describing the defect, and relocating on it is churn with no evidence
                        // behind it.
                        if (depth[i - 1] <= 0 || depth[i] < 0)
                        {
                            continue;
                        }

                        int end = i;
                        bool delimited = false;
                        bool resetsCarry = false;
                        int exitDepth = -1;
                        while (end < instructions.Count)
                        {
                            if (end > i && targets.Contains(instructions[end]))
                            {
                                break;      // a second entry point: not a single block
                            }

                            Code code = instructions[end].OpCode.Code;
                            if (code == Code.Ret || code == Code.Throw)
                            {
                                delimited = true;
                                resetsCarry = true;
                                break;
                            }

                            if (code == Code.Br || code == Code.Br_S)
                            {
                                if (!MonoRelocationBrEnabled)
                                {
                                    break;  // default (v27/v29): a br-terminated block is left alone
                                }

                                // br neither pops nor pushes, so its entry depth IS what the
                                // block hands to whatever follows the clone.
                                delimited = true;
                                exitDepth = depth[end];
                                break;
                            }

                            end++;
                        }

                        if (delimited && (resetsCarry || exitDepth >= 0))
                        {
                            candidates.Add(new RelocatableBlock(i, end, resetsCarry, depth[i], exitDepth));
                        }
                    }

                    if (candidates.Count == 0)
                    {
                        continue;
                    }

                    // Clones are appended back to back, so each one's terminator is carried into
                    // the next — exactly the defect being removed. Order them so that never
                    // happens: resets first, then the br-terminated ones as a chain where each
                    // exit depth feeds the next entry depth. See the doc comment.
                    List<RelocatableBlock> ordered = new List<RelocatableBlock>(candidates.Count);
                    List<RelocatableBlock> chainable = new List<RelocatableBlock>();
                    foreach (RelocatableBlock candidate in candidates)
                    {
                        if (candidate.ResetsCarry)
                        {
                            ordered.Add(candidate);
                        }
                        else
                        {
                            chainable.Add(candidate);
                        }
                    }

                    // -1 means "a reset precedes this slot", so the first link is unconstrained:
                    // either a ret/throw clone above, or the method's own terminator.
                    int carried = -1;
                    while (chainable.Count > 0)
                    {
                        int pick = -1;
                        for (int k = 0; k < chainable.Count; k++)
                        {
                            if (carried < 0 || chainable[k].EntryDepth == carried)
                            {
                                pick = k;
                                break;
                            }
                        }

                        if (pick < 0)
                        {
                            break;      // nothing left fits the carry: leave the rest alone
                        }

                        ordered.Add(chainable[pick]);
                        carried = chainable[pick].ExitDepth;
                        chainable.RemoveAt(pick);
                    }

                    ILProcessor il = body.GetILProcessor();
                    int done = 0;

                    foreach (RelocatableBlock candidate in ordered)
                    {
                        List<Instruction> clone = new List<Instruction>();
                        bool copyable = true;

                        for (int k = candidate.Start; k <= candidate.End; k++)
                        {
                            Instruction copy = CloneInstruction(instructions[k]);
                            if (copy == null)
                            {
                                copyable = false;
                                break;
                            }

                            clone.Add(copy);
                        }

                        if (!copyable)
                        {
                            continue;
                        }

                        // Branches inside the block keep their original targets, which is correct:
                        // the block has no internal entry point, so nothing needs remapping.
                        Instruction tail = body.Instructions[body.Instructions.Count - 1];
                        foreach (Instruction ins in clone)
                        {
                            il.InsertAfter(tail, ins);
                            tail = ins;
                        }

                        Instruction head = instructions[candidate.Start];
                        Instruction newHead = clone[0];
                        HashSet<Instruction> cloned = new HashSet<Instruction>(clone);

                        foreach (Instruction ins in body.Instructions)
                        {
                            if (cloned.Contains(ins))
                            {
                                continue;
                            }

                            if (ins.Operand is Instruction single)
                            {
                                if (single == head)
                                {
                                    ins.Operand = newHead;
                                }
                            }
                            else if (ins.Operand is Instruction[] many)
                            {
                                bool changed = false;
                                Instruction[] rebound = (Instruction[])many.Clone();
                                for (int k = 0; k < rebound.Length; k++)
                                {
                                    if (rebound[k] == head)
                                    {
                                        rebound[k] = newHead;
                                        changed = true;
                                    }
                                }

                                if (changed)
                                {
                                    ins.Operand = rebound;
                                }
                            }
                        }

                        done++;
                    }

                    if (done > 0)
                    {
                        // A cloned short branch now sits far from its target, which the one-byte
                        // form cannot encode (ILVerify: BadJumpTarget). Expand everything, then
                        // let Cecil re-shorten whatever still fits.
                        body.SimplifyMacros();
                        body.OptimizeMacros();

                        relocated += done;
                        methodsTouched++;
                    }
                }
            }

            if (relocated > 0)
            {
                Log.Info(LogCategory.AppInstall,
                    $"[mono-fixup] {moduleName}: relocated {relocated} block(s) across " +
                    $"{methodsTouched} method(s) that MonoVM would refuse to compile.");
            }
        }

        /// <summary>
        /// One block <see cref="RelocateMonoStackConflictBlocks"/> intends to copy to the end of
        /// its method: a start/end index pair into the ORIGINAL instruction list, plus whether
        /// control leaves the block with the evaluation stack empty.
        ///
        /// <para>The depths are not decoration: they decide where the clone may be appended,
        /// because a clone's terminator is carried into whatever is appended next. See the
        /// ordering in the pass.</para>
        /// </summary>
        private readonly struct RelocatableBlock
        {
            public RelocatableBlock(int start, int end, bool resetsCarry, int entryDepth, int exitDepth)
            {
                Start = start;
                End = end;
                ResetsCarry = resetsCarry;
                EntryDepth = entryDepth;
                ExitDepth = exitDepth;
            }

            /// <summary>Index of the block's first instruction, inclusive.</summary>
            public int Start { get; }

            /// <summary>Index of the block's terminator (ret, throw or br), inclusive.</summary>
            public int End { get; }

            /// <summary>
            /// True when the block ends in ret or throw, which ends the flow and so leaves
            /// whatever is appended next unconstrained.
            /// </summary>
            public bool ResetsCarry { get; }

            /// <summary>Stack depth the block's entrants arrive at.</summary>
            public int EntryDepth { get; }

            /// <summary>
            /// Stack depth handed to the next appended clone, or -1 when
            /// <see cref="ResetsCarry"/> makes the question moot.
            /// </summary>
            public int ExitDepth { get; }
        }

        /// <summary>
        /// Entry stack depth for each instruction by abstract interpretation, or -1 where the
        /// instruction is unreachable. It is used to tell "this br left the stack empty"
        /// (harmless) from "it left something behind" (the defect above), both for the branch
        /// that precedes a block and for the branch that ends one, so it deliberately does not
        /// model types -- just how many slots are live.
        /// </summary>
        private static int[] ComputeEntryStackDepths(List<Instruction> instructions)
        {
            Dictionary<Instruction, int> index = new Dictionary<Instruction, int>(instructions.Count);
            for (int i = 0; i < instructions.Count; i++)
            {
                index[instructions[i]] = i;
            }

            int[] depth = new int[instructions.Count];
            for (int i = 0; i < depth.Length; i++)
            {
                depth[i] = -1;
            }

            Queue<KeyValuePair<int, int>> work = new Queue<KeyValuePair<int, int>>();
            work.Enqueue(new KeyValuePair<int, int>(0, 0));

            while (work.Count > 0)
            {
                KeyValuePair<int, int> item = work.Dequeue();
                int at = item.Key;
                int current = item.Value;

                while (true)
                {
                    if (at < 0 || at >= instructions.Count || depth[at] >= 0)
                    {
                        break;
                    }

                    depth[at] = current;
                    Instruction ins = instructions[at];

                    current -= PopCount(ins, current);
                    if (current < 0)
                    {
                        current = 0;
                    }

                    current += PushCount(ins);

                    FlowControl flow = ins.OpCode.FlowControl;
                    if (flow == FlowControl.Branch)
                    {
                        if (ins.Operand is Instruction target && index.TryGetValue(target, out int next))
                        {
                            at = next;
                            continue;
                        }

                        break;
                    }

                    if (flow == FlowControl.Cond_Branch)
                    {
                        if (ins.Operand is Instruction[] cases)
                        {
                            foreach (Instruction c in cases)
                            {
                                if (index.TryGetValue(c, out int ci))
                                {
                                    work.Enqueue(new KeyValuePair<int, int>(ci, current));
                                }
                            }
                        }
                        else if (ins.Operand is Instruction conditional
                                 && index.TryGetValue(conditional, out int ti))
                        {
                            work.Enqueue(new KeyValuePair<int, int>(ti, current));
                        }

                        at++;
                        continue;
                    }

                    if (flow == FlowControl.Return || flow == FlowControl.Throw)
                    {
                        break;
                    }

                    at++;
                }
            }

            return depth;
        }

        private static int PopCount(Instruction ins, int current)
        {
            switch (ins.OpCode.StackBehaviourPop)
            {
                case StackBehaviour.Pop0:
                    return 0;

                case StackBehaviour.Pop1:
                case StackBehaviour.Popi:
                case StackBehaviour.Popref:
                    return 1;

                case StackBehaviour.Pop1_pop1:
                case StackBehaviour.Popi_pop1:
                case StackBehaviour.Popi_popi:
                case StackBehaviour.Popi_popi8:
                case StackBehaviour.Popi_popr4:
                case StackBehaviour.Popi_popr8:
                case StackBehaviour.Popref_pop1:
                case StackBehaviour.Popref_popi:
                    return 2;

                case StackBehaviour.Popi_popi_popi:
                case StackBehaviour.Popref_popi_popi:
                case StackBehaviour.Popref_popi_popi8:
                case StackBehaviour.Popref_popi_popr4:
                case StackBehaviour.Popref_popi_popr8:
                case StackBehaviour.Popref_popi_popref:
                    return 3;

                case StackBehaviour.PopAll:
                    return current;

                case StackBehaviour.Varpop:
                    if (ins.OpCode.Code == Code.Ret)
                    {
                        return current;
                    }

                    if (ins.Operand is MethodReference callee)
                    {
                        int popped = callee.Parameters.Count;
                        if (callee.HasThis && ins.OpCode.Code != Code.Newobj)
                        {
                            popped++;
                        }

                        return popped;
                    }

                    return 0;

                default:
                    return 0;
            }
        }

        private static int PushCount(Instruction ins)
        {
            switch (ins.OpCode.StackBehaviourPush)
            {
                case StackBehaviour.Push1:
                case StackBehaviour.Pushi:
                case StackBehaviour.Pushi8:
                case StackBehaviour.Pushr4:
                case StackBehaviour.Pushr8:
                case StackBehaviour.Pushref:
                    return 1;

                case StackBehaviour.Push1_push1:
                    return 2;

                case StackBehaviour.Varpush:
                    return ins.Operand is MethodReference callee
                           && callee.ReturnType.FullName != "System.Void" ? 1 : 0;

                default:
                    return 0;
            }
        }

        /// <summary>
        /// A structural copy of one instruction. Returns null for an operand shape this pass does
        /// not know how to reproduce exactly, which makes the caller leave that whole block alone
        /// -- declining to copy costs one game one workaround, guessing would corrupt its IL.
        /// </summary>
        private static Instruction CloneInstruction(Instruction ins)
        {
            switch (ins.OpCode.OperandType)
            {
                case OperandType.InlineNone:
                    return Instruction.Create(ins.OpCode);

                case OperandType.InlineBrTarget:
                case OperandType.ShortInlineBrTarget:
                    return Instruction.Create(ins.OpCode, (Instruction)ins.Operand);

                case OperandType.InlineSwitch:
                    return Instruction.Create(ins.OpCode, (Instruction[])ins.Operand);

                case OperandType.InlineField:
                    return Instruction.Create(ins.OpCode, (FieldReference)ins.Operand);

                case OperandType.InlineMethod:
                    return Instruction.Create(ins.OpCode, (MethodReference)ins.Operand);

                case OperandType.InlineType:
                    return Instruction.Create(ins.OpCode, (TypeReference)ins.Operand);

                // ldtoken: the operand may be a type, a field OR a method, so all three are
                // handled rather than assuming the common case.
                case OperandType.InlineTok:
                    if (ins.Operand is TypeReference typeToken)
                    {
                        return Instruction.Create(ins.OpCode, typeToken);
                    }

                    if (ins.Operand is FieldReference fieldToken)
                    {
                        return Instruction.Create(ins.OpCode, fieldToken);
                    }

                    if (ins.Operand is MethodReference methodToken)
                    {
                        return Instruction.Create(ins.OpCode, methodToken);
                    }

                    return null;

                case OperandType.InlineI:
                    return Instruction.Create(ins.OpCode, (int)ins.Operand);

                case OperandType.InlineI8:
                    return Instruction.Create(ins.OpCode, (long)ins.Operand);

                case OperandType.ShortInlineI:
                    return ins.OpCode.Code == Code.Ldc_I4_S
                        ? Instruction.Create(ins.OpCode, (sbyte)ins.Operand)
                        : Instruction.Create(ins.OpCode, (byte)ins.Operand);

                case OperandType.InlineR:
                    return Instruction.Create(ins.OpCode, (double)ins.Operand);

                case OperandType.ShortInlineR:
                    return Instruction.Create(ins.OpCode, (float)ins.Operand);

                case OperandType.InlineString:
                    return Instruction.Create(ins.OpCode, (string)ins.Operand);

                case OperandType.InlineVar:
                case OperandType.ShortInlineVar:
                    return Instruction.Create(ins.OpCode, (VariableDefinition)ins.Operand);

                case OperandType.InlineArg:
                case OperandType.ShortInlineArg:
                    return Instruction.Create(ins.OpCode, (ParameterDefinition)ins.Operand);

                default:
                    return null;
            }
        }

        /// <summary>
        /// Records every TypeDef's <em>pristine</em> metadata token in an embedded table, and
        /// sends every <c>MemberInfo.get_MetadataToken</c> call site in <paramref name="module"/>
        /// to <see cref="WPR.WindowsCompability.OriginalMetadataTokens.Resolve"/>, which reads it.
        /// That type carries the full account of why this is needed; the short version is that
        /// <b>Cecil does not preserve TypeDef row ids</b> — it re-emits the table depth first,
        /// parent immediately followed by its nested types — so an assembly laid out any other way
        /// comes back renumbered even though nothing about its types changed.
        ///
        /// <para>Ordinary game code never notices. Eazfuscator.NET does: it derives its
        /// string-decryption key from the tokens of its own helper types and uses the result as a
        /// byte offset into the encrypted string blob, so a renumbered table makes the game seek
        /// a stream to a nonsense position and die on the first string it decrypts.</para>
        ///
        /// <para>The pass is a no-op for a module that never asks for a token, which is all but
        /// two games in a 307-XAP library (The Treasures of Montezuma and Farm Frenzy 2 — the same
        /// Alawar/YF engine, eight obfuscated assemblies each). It deliberately does nothing at all
        /// for a module that also calls the <c>Module.Resolve*</c> family: such a module feeds
        /// tokens back to the runtime, and only types are remapped here, so a half-remapped
        /// round-trip would be worse than none. In this library that is UnityEngine alone.</para>
        /// </summary>
        private static void PreserveOriginalMetadataTokens(ModuleDefinition module, string fileName)
        {
            const string MemberInfoTypeName = "System.Reflection.MemberInfo";
            const string ModuleTypeName = "System.Reflection.Module";

            List<Instruction> sites = new List<Instruction>();
            bool resolvesTokens = false;

            foreach (TypeDefinition type in module.GetTypes())
            {
                foreach (MethodDefinition method in type.Methods)
                {
                    if (!method.HasBody)
                    {
                        continue;
                    }

                    foreach (Instruction ins in method.Body.Instructions)
                    {
                        if (ins.OpCode != OpCodes.Callvirt && ins.OpCode != OpCodes.Call)
                        {
                            continue;
                        }

                        if (ins.Operand is not MethodReference callee || callee.DeclaringType == null)
                        {
                            continue;
                        }

                        /* get_MetadataToken is declared on MemberInfo and inherited, so a call
                         * site may name MemberInfo, Type, MethodBase, FieldInfo or any other
                         * subclass. Match on the name and arity instead of the declaring type. */
                        if (callee.Name == "get_MetadataToken" && callee.Parameters.Count == 0)
                        {
                            sites.Add(ins);
                            continue;
                        }

                        if (callee.DeclaringType.FullName == ModuleTypeName
                            && callee.Name.StartsWith("Resolve", StringComparison.Ordinal))
                        {
                            resolvesTokens = true;
                        }
                    }
                }
            }

            if (sites.Count == 0)
            {
                return;
            }

            if (resolvesTokens)
            {
                Log.Info(LogCategory.AppInstall,
                    $"[token-fixup] {fileName}: {sites.Count} get_MetadataToken call site(s) left "
                    + "alone — the module also calls Module.Resolve*, so its tokens make a "
                    + "round trip this pass cannot invert.");
                return;
            }

            /* Tokens are still the ones read from disk: nothing above adds or removes a type, and
             * Cecil only reassigns them while writing. So this is the pristine numbering. */
            List<KeyValuePair<string, int>> entries = new List<KeyValuePair<string, int>>();
            foreach (TypeDefinition type in module.GetTypes())
            {
                if (type.FullName == "<Module>")
                {
                    continue;
                }

                // Cecil spells a nested type Outer/Inner; reflection spells it Outer+Inner, and
                // reflection is what reads this table back.
                entries.Add(new KeyValuePair<string, int>(
                    type.FullName.Replace('/', '+'), type.MetadataToken.ToInt32()));
            }

            byte[] table;
            using (MemoryStream buffer = new MemoryStream())
            {
                using (BinaryWriter writer =
                    new BinaryWriter(buffer, System.Text.Encoding.UTF8, true))
                {
                    writer.Write(entries.Count);
                    foreach (KeyValuePair<string, int> entry in entries)
                    {
                        writer.Write(entry.Key);
                        writer.Write(entry.Value);
                    }
                }

                table = buffer.ToArray();
            }

            string resourceName = WPR.WindowsCompability.OriginalMetadataTokens.ResourceName;
            for (int i = module.Resources.Count - 1; i >= 0; i--)
            {
                // Defensive: PatchDll always starts from the pristine .original, so a table
                // should never already be there. Replace rather than end up with two.
                if (module.Resources[i].Name == resourceName)
                {
                    module.Resources.RemoveAt(i);
                }
            }

            module.Resources.Add(new EmbeddedResource(
                resourceName, ManifestResourceAttributes.Private, table));

            System.Reflection.MethodInfo shim =
                typeof(WPR.WindowsCompability.OriginalMetadataTokens)
                    .GetMethod(nameof(WPR.WindowsCompability.OriginalMetadataTokens.Resolve))!;
            MethodReference target = module.ImportReference(shim);

            foreach (Instruction ins in sites)
            {
                // callvirt -> call: the shim is static and the instance is now argument zero.
                // Nothing else about the stack changes.
                ins.OpCode = OpCodes.Call;
                ins.Operand = target;
            }

            Log.Info(LogCategory.AppInstall,
                $"[token-fixup] {fileName}: {sites.Count} get_MetadataToken call site(s) now read "
                + $"the pristine token of {entries.Count} type(s).");
        }

        /// <summary>
        /// Every path-taking <see cref="System.IO.IsolatedStorage.IsolatedStorageFile"/> call site
        /// in <paramref name="module"/> is retargeted to the matching static on
        /// <see cref="WPR.WindowsCompability.SharedIsolatedStorage"/>, with the instance becoming
        /// argument zero. That type carries the full account of what the shims change and why;
        /// in short, opens get a shared <c>FileShare</c> (v20) and everything gets its separators
        /// normalised for the running platform (v31).
        ///
        /// <para>A call-site rewrite rather than a <see cref="MemberPatches"/> entry because
        /// <c>IsolatedStorageFile</c> is sealed, so nothing can be substituted for the instance on
        /// the stack.</para>
        ///
        /// <para>The member list below is the gate; the shim is then found by name and arity, so
        /// adding an overload to <c>SharedIsolatedStorage</c> is enough to cover it. A member named
        /// here with no matching shim is reported once and left alone — never guessed at.</para>
        /// </summary>
        private static void RedirectIsolatedStorageCalls(ModuleDefinition module)
        {
            const string StoreTypeName = "System.IO.IsolatedStorage.IsolatedStorageFile";

            // Keyed by "<name>/<instance arg count>" — the overloads differ only in arity, and
            // importing the same MethodInfo repeatedly would add a member ref per call site.
            Dictionary<string, MethodReference?> imported = new Dictionary<string, MethodReference?>();
            int rewritten = 0;

            foreach (TypeDefinition type in module.GetTypes())
            {
                foreach (MethodDefinition method in type.Methods)
                {
                    if (!method.HasBody)
                    {
                        continue;
                    }

                    foreach (Instruction ins in method.Body.Instructions)
                    {
                        if (ins.OpCode != OpCodes.Callvirt && ins.OpCode != OpCodes.Call)
                        {
                            continue;
                        }

                        if (ins.Operand is not MethodReference callee
                            || callee.DeclaringType == null
                            || callee.DeclaringType.FullName != StoreTypeName
                            || !(callee.HasThis
                                ? IsolatedStorageRedirects.Contains(callee.Name)
                                : IsolatedStorageStaticRedirects.Contains(callee.Name)))
                        {
                            continue;
                        }

                        // A static keeps its own arity; an instance call gains the store as
                        // argument zero.
                        int shimArity = callee.Parameters.Count + (callee.HasThis ? 1 : 0);
                        string key = callee.Name + "/" + shimArity;
                        if (!imported.TryGetValue(key, out MethodReference? target))
                        {
                            // The shim's signature is the instance one with the store prepended.
                            // Fully qualified: System.Reflection can't be imported at file scope
                            // here — MethodAttributes/PropertyAttributes would go ambiguous
                            // against Mono.Cecil's.
                            System.Reflection.MethodInfo? shim =
                                typeof(WPR.WindowsCompability.SharedIsolatedStorage)
                                    .GetMethods(System.Reflection.BindingFlags.Public
                                        | System.Reflection.BindingFlags.Static)
                                    .FirstOrDefault(m => m.Name == callee.Name
                                        && m.GetParameters().Length == shimArity);

                            target = shim == null ? null : module.ImportReference(shim);
                            imported[key] = target;

                            if (shim == null)
                            {
                                // An overload we don't mirror. Leave it alone rather than guess —
                                // it keeps the BCL's exclusive share and the old behaviour.
                                Debug.WriteLine($"[iso-fixup] no shim for {StoreTypeName}::{callee.Name}"
                                    + $" with {callee.Parameters.Count} arg(s) — left as-is.");
                            }
                        }

                        if (target == null)
                        {
                            continue;
                        }

                        // callvirt -> call: the shim is static, and the instance is now argument
                        // zero. Nothing else about the stack changes.
                        ins.OpCode = OpCodes.Call;
                        ins.Operand = target;
                        rewritten += 1;
                    }
                }
            }

            if (rewritten > 0)
            {
                Debug.WriteLine($"[iso-fixup] redirected {rewritten} IsolatedStorageFile call(s)"
                    + $" in {module.Name} to the shim.");
            }
        }

        /// <summary>
        /// Every <see cref="System.Net.Sockets.Socket"/> <c>*Async(SocketAsyncEventArgs)</c> call
        /// site in <paramref name="module"/> is retargeted to the matching static on
        /// <see cref="WPR.WindowsCompability.SilverlightSockets"/>, the socket becoming argument
        /// zero (the static <c>ConnectAsync(SocketType, ProtocolType, e)</c> keeps its arity).
        /// That type explains why: on Silverlight these always completed asynchronously and
        /// raised <c>Completed</c>; .NET may finish at once, return false and raise nothing.
        ///
        /// <para>A call-site rewrite because the game constructs the <c>Socket</c> itself, so no
        /// subclass can stand where the instance is.</para>
        /// </summary>
        private static void RedirectSocketCalls(ModuleDefinition module)
        {
            const string SocketTypeName = "System.Net.Sockets.Socket";
            Dictionary<string, MethodReference?> imported = new Dictionary<string, MethodReference?>();
            int rewritten = 0;

            foreach (TypeDefinition type in module.GetTypes())
            {
                foreach (MethodDefinition method in type.Methods)
                {
                    if (!method.HasBody)
                    {
                        continue;
                    }

                    foreach (Instruction ins in method.Body.Instructions)
                    {
                        if ((ins.OpCode == OpCodes.Callvirt || ins.OpCode == OpCodes.Call)
                            && ins.Operand is MethodReference handlerRef
                            && handlerRef.DeclaringType?.FullName == "System.Net.Sockets.SocketAsyncEventArgs"
                            && handlerRef.HasThis && handlerRef.Parameters.Count == 1
                            && (handlerRef.Name == "add_Completed" || handlerRef.Name == "remove_Completed"))
                        {
                            // e.Completed += handler: the shim wraps it so a receive's callback
                            // runs between the game's frames.
                            string shimName = handlerRef.Name == "add_Completed" ? "AddCompleted" : "RemoveCompleted";
                            if (!imported.TryGetValue(shimName, out MethodReference? handlerShim))
                            {
                                handlerShim = module.ImportReference(typeof(WPR.WindowsCompability.SilverlightSockets).GetMethod(shimName)!);
                                imported[shimName] = handlerShim;
                            }
                            ins.OpCode = OpCodes.Call;
                            ins.Operand = handlerShim;
                            rewritten += 1;
                            continue;
                        }

                        if ((ins.OpCode != OpCodes.Callvirt && ins.OpCode != OpCodes.Call)
                            || ins.Operand is not MethodReference callee
                            || callee.DeclaringType?.FullName != SocketTypeName
                            || !SocketAsyncRedirects.Contains(callee.Name)
                            || callee.Parameters.Count == 0
                            || callee.Parameters[^1].ParameterType.FullName != "System.Net.Sockets.SocketAsyncEventArgs"
                            || callee.ReturnType.FullName != "System.Boolean")
                        {
                            continue;
                        }

                        int shimArity = callee.Parameters.Count + (callee.HasThis ? 1 : 0);
                        string key = callee.Name + "/" + shimArity;
                        if (!imported.TryGetValue(key, out MethodReference? target))
                        {
                            System.Reflection.MethodInfo? shim =
                                typeof(WPR.WindowsCompability.SilverlightSockets)
                                    .GetMethods(System.Reflection.BindingFlags.Public
                                        | System.Reflection.BindingFlags.Static)
                                    .FirstOrDefault(m => m.Name == callee.Name
                                        && m.GetParameters().Length == shimArity
                                        && (callee.HasThis
                                            ? m.GetParameters()[0].ParameterType == typeof(System.Net.Sockets.Socket)
                                            : m.GetParameters()[0].ParameterType != typeof(System.Net.Sockets.Socket)));

                            target = shim == null ? null : module.ImportReference(shim);
                            imported[key] = target;
                            if (shim == null)
                            {
                                Debug.WriteLine($"[socket-fixup] no shim for {SocketTypeName}::{callee.Name}"
                                    + $" with {callee.Parameters.Count} arg(s) — left as-is.");
                            }
                        }

                        if (target == null)
                        {
                            continue;
                        }

                        ins.OpCode = OpCodes.Call;
                        ins.Operand = target;
                        rewritten += 1;
                    }
                }
            }

            if (rewritten > 0)
            {
                Log.Info(LogCategory.AppInstall,
                    $"[socket-fixup] {module.Name}: {rewritten} Socket *Async call(s) now always complete asynchronously.");
            }
        }

        /// <summary>
        /// Every <c>System.Reflection.Module::get_Name</c> call site in <paramref name="module"/>
        /// is retargeted to <see cref="WPR.WindowsCompability.ReflectionModules.GetName"/>, the
        /// module becoming argument zero. Game assemblies are loaded from a stream, so .NET names
        /// their modules <c>"&lt;Unknown&gt;"</c> where the phone gave the file name; that type
        /// has the account. A call-site rewrite because <c>Module</c>'s runtime type is the
        /// runtime's own, so nothing can be substituted for the instance on the stack.
        /// </summary>
        private static void RedirectModuleNameCalls(ModuleDefinition module)
        {
            MethodReference? target = null;
            int rewritten = 0;

            foreach (TypeDefinition type in module.GetTypes())
            {
                foreach (MethodDefinition method in type.Methods)
                {
                    if (!method.HasBody)
                    {
                        continue;
                    }

                    foreach (Instruction ins in method.Body.Instructions)
                    {
                        if ((ins.OpCode != OpCodes.Callvirt && ins.OpCode != OpCodes.Call)
                            || ins.Operand is not MethodReference callee
                            || callee.Name != "get_Name"
                            || !callee.HasThis
                            || callee.Parameters.Count != 0
                            || callee.DeclaringType?.FullName != "System.Reflection.Module")
                        {
                            continue;
                        }

                        target ??= module.ImportReference(
                            typeof(WPR.WindowsCompability.ReflectionModules).GetMethod(
                                nameof(WPR.WindowsCompability.ReflectionModules.GetName))!);
                        ins.OpCode = OpCodes.Call;
                        ins.Operand = target;
                        rewritten += 1;
                    }
                }
            }

            if (rewritten > 0)
            {
                Log.Info(LogCategory.AppInstall,
                    $"[module-name-fixup] {module.Name}: {rewritten} Module.Name call(s) now report the file name.");
            }
        }

        /// <summary>
        /// Every named <c>System.Threading.Mutex</c> constructor (<c>(bool, string)</c>,
        /// <c>(bool, string, out bool)</c>) becomes a call to
        /// <see cref="WPR.WindowsCompability.NamedMutex.Create(bool, string)"/>, and
        /// <c>Mutex.OpenExisting(string)</c> goes to its twin. Android cannot create a named mutex
        /// at all; that type has the account. A call-site rewrite because <c>Mutex</c> is sealed:
        /// <c>newobj</c> becomes a static <c>call</c> with the same arguments and the same result
        /// on the stack.
        /// </summary>
        private static void RedirectNamedMutexCalls(ModuleDefinition module)
        {
            const string MutexTypeName = "System.Threading.Mutex";
            Dictionary<int, MethodReference> creators = new Dictionary<int, MethodReference>();
            MethodReference? openExisting = null;
            int rewritten = 0;

            foreach (TypeDefinition type in module.GetTypes())
            {
                foreach (MethodDefinition method in type.Methods)
                {
                    if (!method.HasBody)
                    {
                        continue;
                    }

                    foreach (Instruction ins in method.Body.Instructions)
                    {
                        if (ins.Operand is not MethodReference callee
                            || callee.DeclaringType?.FullName != MutexTypeName)
                        {
                            continue;
                        }

                        if (ins.OpCode == OpCodes.Newobj
                            && callee.Name == ".ctor"
                            && callee.Parameters.Count is 2 or 3
                            && callee.Parameters[0].ParameterType.FullName == "System.Boolean"
                            && callee.Parameters[1].ParameterType.FullName == "System.String"
                            && (callee.Parameters.Count == 2
                                || callee.Parameters[2].ParameterType.FullName == "System.Boolean&"))
                        {
                            int arity = callee.Parameters.Count;
                            if (!creators.TryGetValue(arity, out MethodReference? target))
                            {
                                System.Type[] parameters = arity == 2
                                    ? new[] { typeof(bool), typeof(string) }
                                    : new[] { typeof(bool), typeof(string), typeof(bool).MakeByRefType() };
                                target = module.ImportReference(
                                    typeof(WPR.WindowsCompability.NamedMutex).GetMethod(
                                        nameof(WPR.WindowsCompability.NamedMutex.Create), parameters)!);
                                creators[arity] = target;
                            }

                            ins.OpCode = OpCodes.Call;
                            ins.Operand = target;
                            rewritten += 1;
                        }
                        else if (ins.OpCode == OpCodes.Call
                            && callee.Name == "OpenExisting"
                            && !callee.HasThis
                            && callee.Parameters.Count == 1)
                        {
                            openExisting ??= module.ImportReference(
                                typeof(WPR.WindowsCompability.NamedMutex).GetMethod(
                                    nameof(WPR.WindowsCompability.NamedMutex.OpenExisting))!);
                            ins.Operand = openExisting;
                            rewritten += 1;
                        }
                    }
                }
            }

            if (rewritten > 0)
            {
                Log.Info(LogCategory.AppInstall,
                    $"[mutex-fixup] {module.Name}: {rewritten} named Mutex call(s) now go through NamedMutex.");
            }
        }

        private static readonly HashSet<string> SocketAsyncRedirects =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "ConnectAsync", "SendAsync", "ReceiveAsync", "SendToAsync", "ReceiveFromAsync",
            };

        /// <summary>
        /// The <c>IsolatedStorageFile</c> members whose call sites are redirected. Every one of
        /// them takes at least one store-relative path or search pattern, which is the entire
        /// reason they are listed: a WP7 title may spell any of them with a Windows separator, and
        /// on Android that is an ordinary filename character.
        ///
        /// <para><c>GetUserStoreForApplication</c> is not here because it is static — see
        /// <see cref="IsolatedStorageStaticRedirects"/>. The parameterless <c>GetFileNames()</c> / <c>GetDirectoryNames()</c> overloads
        /// are reached by name but find no shim (there is nothing to normalise) and are left alone
        /// — see the note on <c>SharedIsolatedStorage</c>.</para>
        /// </summary>
        private static readonly HashSet<string> IsolatedStorageRedirects =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "OpenFile",
                "CreateFile",
                "GetFileNames",
                "GetDirectoryNames",
                "DirectoryExists",
                "FileExists",
                "CreateDirectory",
                "DeleteDirectory",
                "DeleteFile",
                "MoveFile",
                "MoveDirectory",
                "CopyFile",
                "GetCreationTime",
                "GetLastAccessTime",
                "GetLastWriteTime",
            };

        /// <summary>
        /// The static <c>IsolatedStorageFile</c> members whose call sites are redirected (v38).
        /// <c>GetUserStoreForApplication</c> is how a WP7 title obtains its store, and the BCL
        /// answers it with ONE store for the whole host, so every game shared it and titles with
        /// a common filename overwrote each other — see <c>PerGameIsolatedStorage</c>. It is the
        /// only store accessor WP7 exposed, so it is the only one listed.
        /// </summary>
        private static readonly HashSet<string> IsolatedStorageStaticRedirects =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "GetUserStoreForApplication",
            };

        /// <summary>
        /// Makes Eazfuscator.NET's caller-identity checks answer "trusted", so a game's encrypted
        /// strings decrypt to what they say rather than to a tamper sentinel.
        ///
        /// <para>The obfuscator's string decryptor verifies WHO IS CALLING IT by walking
        /// <see cref="System.Diagnostics.StackTrace"/> to a FIXED frame index and demanding that
        /// frame's declaring type live in the decryptor's own assembly. There are two such checks:
        /// one folds its verdict into a poison flag, and once that flag is set every single string
        /// in the assembly decrypts to the literal <c>"X0X"</c>; the other returns a bool that is
        /// XORed into the decryption key itself. Both have to be forced, and forcing only the
        /// first is actively worse — the sentinel stops appearing, the key is still wrong, and the
        /// blob reader runs off the end with "Attempted to read past the end of the stream".</para>
        ///
        /// <para><b>This is not the same bug as <see cref="PreserveOriginalMetadataTokens"/>,
        /// though it is the same obfuscator and the same game.</b> That pass repairs a key input we
        /// broke ourselves (Cecil renumbers the TypeDef table). This one is a check that WPR does
        /// not perturb at all: the patched bytes are irrelevant. Proven by pushing the desktop's
        /// byte-identical patched assemblies onto the phone — desktop plays, phone still poisons —
        /// so what differs is the managed stack the runtime reports, not the file.</para>
        ///
        /// <para><b>Why it only shows up on Android.</b> The check asks for a specific frame by
        /// index, and CoreCLR-on-Android does not hand back the frame the obfuscator expects.
        /// Eliminated by measurement, so do not re-walk these: the patched bytes differing
        /// (byte-identical assemblies fail the same way), the assembly being loaded twice so the
        /// <c>Assembly</c> instances differ (each game assembly is probed exactly once), and JIT
        /// inlining moving the frames (marking all 8,993 methods across the game's 14 assemblies
        /// <c>NoInlining</c> changed nothing).</para>
        ///
        /// <para>The Treasures of Montezuma (56a2bd8b-af90-4575-b25f-97b31a179422) is the
        /// reference case: nine of its assemblies carry the check, and before this it died in
        /// <c>Game..ctor</c> with "An item with the same key has already been added. Key: X0X" —
        /// two different axis-id strings having both decrypted to the sentinel. Of 48 installed
        /// titles it is the only one that carries this; Farm Frenzy 2 is the likely second, being
        /// the same Alawar/YF engine.</para>
        ///
        /// <para>Forcing the check to its TRUSTED outcome is what keeps this safe on desktop,
        /// where the check already passes: the pass writes the answer the runtime was going to
        /// give anyway. Verified — the same rewritten assemblies still run on Windows.</para>
        /// </summary>
        private static void NeutraliseObfuscatorStackIdentityChecks(
            ModuleDefinition module, string fileName)
        {
            int forcedHelpers = 0;
            int forcedBranches = 0;

            foreach (TypeDefinition type in module.GetTypes())
            {
                foreach (MethodDefinition method in type.Methods)
                {
                    if (!method.HasBody || method.Body.Instructions.Count == 0) continue;

                    /* Three signals together, because any one alone is something ordinary code
                     * does: a StackTrace walk, a typeof(RuntimeMethodHandle) comparison (how the
                     * obfuscator spots a reflection invoke) and a Type.Assembly read. A logger
                     * that walks frames has the first and neither of the others. */
                    bool walksStack = false;
                    bool comparesRuntimeMethodHandle = false;
                    bool readsAssembly = false;

                    foreach (Instruction instruction in method.Body.Instructions)
                    {
                        if (instruction.Operand is MethodReference callee)
                        {
                            if (callee.Name == "GetFrame"
                                && callee.DeclaringType != null
                                && callee.DeclaringType.FullName == "System.Diagnostics.StackTrace")
                            {
                                walksStack = true;
                            }
                            else if (callee.Name == "get_Assembly"
                                && callee.DeclaringType != null
                                && callee.DeclaringType.FullName == "System.Type")
                            {
                                readsAssembly = true;
                            }
                        }
                        else if (instruction.OpCode == OpCodes.Ldtoken
                            && instruction.Operand is TypeReference token
                            && token.FullName == "System.RuntimeMethodHandle")
                        {
                            comparesRuntimeMethodHandle = true;
                        }
                    }

                    if (!walksStack || !comparesRuntimeMethodHandle || !readsAssembly) continue;

                    // A parameterless static bool IS the whole check, so answer it outright.
                    if (method.IsStatic && !method.HasParameters
                        && method.ReturnType.FullName == "System.Boolean")
                    {
                        method.Body.Instructions.Clear();
                        method.Body.Variables.Clear();
                        method.Body.ExceptionHandlers.Clear();
                        ILProcessor answer = method.Body.GetILProcessor();
                        answer.Append(answer.Create(OpCodes.Ldc_I4_1));
                        answer.Append(answer.Create(OpCodes.Ret));
                        forcedHelpers++;
                        continue;
                    }

                    if (ForceCallerAssemblyComparison(method)) forcedBranches++;
                }
            }

            if (forcedHelpers + forcedBranches == 0) return;

            Log.Info(LogCategory.AppInstall,
                $"[eaz-fixup] {fileName}: forced {forcedHelpers} caller-trust helper(s) and "
                + $"{forcedBranches} caller-assembly branch(es) to their trusted outcome, so the "
                + "obfuscator's strings decrypt instead of collapsing to the tamper sentinel.");
        }

        /// <summary>
        /// Rewrites the one <c>caller.Assembly == mine</c> test inside an Eazfuscator caller
        /// check so the equal path always runs. Returns whether anything changed.
        /// </summary>
        private static bool ForceCallerAssemblyComparison(MethodDefinition method)
        {
            /* Both operands of the comparison are on the stack, so the branch cannot simply be
             * deleted — it is replaced by two pops plus, for the equality form, an unconditional
             * jump to the target it would have taken. SimplifyMacros first because the rewrite
             * grows the body and a short branch elsewhere may no longer reach; OptimizeMacros
             * re-shortens what still fits. Same reasoning as RelocateMonoStackConflictBlocks. */
            method.Body.SimplifyMacros();
            try
            {
                var instructions = method.Body.Instructions;

                for (int i = 0; i + 1 < instructions.Count; i++)
                {
                    if (!(instructions[i].Operand is MethodReference callee)
                        || callee.Name != "get_Assembly"
                        || callee.DeclaringType == null
                        || callee.DeclaringType.FullName != "System.Type")
                    {
                        continue;
                    }

                    Instruction branch = instructions[i + 1];
                    bool equalTakesBranch =
                        branch.OpCode == OpCodes.Beq || branch.OpCode == OpCodes.Beq_S;
                    bool equalFallsThrough =
                        branch.OpCode == OpCodes.Bne_Un || branch.OpCode == OpCodes.Bne_Un_S;
                    if (!equalTakesBranch && !equalFallsThrough) continue;

                    Instruction target = branch.Operand as Instruction;
                    if (equalTakesBranch && target == null) continue;

                    ILProcessor rewrite = method.Body.GetILProcessor();
                    Instruction firstPop = rewrite.Create(OpCodes.Pop);
                    Instruction secondPop = rewrite.Create(OpCodes.Pop);

                    rewrite.Replace(branch, firstPop);
                    rewrite.InsertAfter(firstPop, secondPop);
                    if (equalTakesBranch)
                    {
                        rewrite.InsertAfter(secondPop, rewrite.Create(OpCodes.Br, target));
                    }

                    /* Cecil's Replace does not repoint anything that branched AT the instruction
                     * it removed, and an obfuscated method is full of jumps. Leaving a dangling
                     * operand writes a corrupt body that fails to verify rather than throwing
                     * here, so fix them up explicitly — including exception handler bounds. */
                    RepointBranches(method.Body, branch, firstPop);
                    return true;
                }

                return false;
            }
            finally
            {
                method.Body.OptimizeMacros();
            }
        }

        /// <summary>
        /// Points every branch, switch case and exception-handler boundary that referred to
        /// <paramref name="removed"/> at <paramref name="replacement"/>.
        /// </summary>
        private static void RepointBranches(
            MethodBody body, Instruction removed, Instruction replacement)
        {
            foreach (Instruction instruction in body.Instructions)
            {
                if (ReferenceEquals(instruction.Operand, removed))
                {
                    instruction.Operand = replacement;
                }
                else if (instruction.Operand is Instruction[] cases)
                {
                    for (int i = 0; i < cases.Length; i++)
                    {
                        if (ReferenceEquals(cases[i], removed)) cases[i] = replacement;
                    }
                }
            }

            foreach (ExceptionHandler handler in body.ExceptionHandlers)
            {
                if (ReferenceEquals(handler.TryStart, removed)) handler.TryStart = replacement;
                if (ReferenceEquals(handler.TryEnd, removed)) handler.TryEnd = replacement;
                if (ReferenceEquals(handler.HandlerStart, removed)) handler.HandlerStart = replacement;
                if (ReferenceEquals(handler.HandlerEnd, removed)) handler.HandlerEnd = replacement;
                if (ReferenceEquals(handler.FilterStart, removed)) handler.FilterStart = replacement;
            }
        }

        /// <summary>
        /// Empties a <c>MediaPlayer.MediaStateChanged</c> handler that only mirrors the player being
        /// PAUSED, i.e. one whose body asks an <c>IsPaused</c> property that reads
        /// <c>MediaPlayer.State</c>, and calls nothing on <c>MediaPlayer</c> itself.
        ///
        /// <para><b>The pattern.</b> A handler of this shape exists to notice the PHONE pausing the
        /// game's music (the Zune controls on the volume overlay) and to reflect it in the game,
        /// typically by setting a music slider or setting to 0. Under WPR nothing outside the game
        /// can pause its <c>MediaPlayer</c>: <c>GameHasControl</c> is always true and Android
        /// backgrounding does not change <c>MediaPlayer.State</c>. So the only pauses such a handler
        /// ever sees are the game's own, and taking those for the player's is always wrong.</para>
        ///
        /// <para><b>The case that found it.</b> The Treasures of Montezuma (Alawar's YF framework)
        /// builds its options dialog with each level, and the dialog subscribes
        /// <c>if (BackgroundMusicPlayer.IsPaused) musicSlider.Value = 0f;</c>. Every level start
        /// plays a track and pauses it at once for a jingle, so the slider's own callback wrote
        /// <c>MediaPlayer.Volume = 0</c> and saved it: the music turned itself off each level.</para>
        ///
        /// <para>A handler that calls <c>MediaPlayer</c> (resuming, moving to the next song) does
        /// real work on a pause and is left alone. Matched by shape only; nothing here names a
        /// title, and obfuscated names do not matter.</para>
        /// </summary>
        private static void EmptyPauseMirroringMediaStateHandlers(ModuleDefinition module)
        {
            static bool IsMediaPlayer(MethodReference r) =>
                r.DeclaringType.Name == "MediaPlayer" && r.DeclaringType.Namespace == "Microsoft.Xna.Framework.Media";

            static bool ReadsPlayerState(MethodReference getter)
            {
                if (getter.Name != "get_IsPaused" || getter.Parameters.Count != 0) return false;
                MethodDefinition? def;
                try { def = getter.Resolve(); } catch { return false; }
                return def is { HasBody: true }
                       && def.Body.Instructions.Any(x => x.Operand is MethodReference r && r.Name == "get_State" && IsMediaPlayer(r));
            }

            try
            {
                var handlers = new HashSet<MethodDefinition>();
                foreach (TypeDefinition type in module.GetTypes())
                {
                    foreach (MethodDefinition m in type.Methods)
                    {
                        if (!m.HasBody) continue;
                        var ins = m.Body.Instructions;
                        if (!ins.Any(i => i.Operand is MethodReference r && r.Name == "add_MediaStateChanged" && IsMediaPlayer(r)))
                        {
                            continue;
                        }

                        foreach (Instruction i in ins)
                        {
                            if (i.OpCode != OpCodes.Ldftn || i.Operand is not MethodReference target) continue;
                            MethodDefinition? def = target.Resolve();
                            if (def == null || !def.HasBody
                                || def.ReturnType.MetadataType != MetadataType.Void
                                || def.Parameters.Count != 2
                                || def.Parameters[1].ParameterType.Name != "EventArgs")
                            {
                                continue;
                            }

                            var calls = def.Body.Instructions.Select(x => x.Operand).OfType<MethodReference>().ToList();
                            if (calls.Any(ReadsPlayerState) && !calls.Any(IsMediaPlayer))
                            {
                                handlers.Add(def);
                            }
                        }
                    }
                }

                if (handlers.Count == 0) return;

                foreach (MethodDefinition h in handlers)
                {
                    h.Body.Instructions.Clear();
                    h.Body.ExceptionHandlers.Clear();
                    h.Body.Variables.Clear();
                    h.Body.InitLocals = false;
                    h.Body.GetILProcessor().Append(Instruction.Create(OpCodes.Ret));
                }

                Log.Info(LogCategory.AppInstall,
                    $"[pause-mirror] {module.Name}: emptied {handlers.Count} MediaStateChanged handler(s) that only mirror the player being paused (only the game itself can pause it here).");
            }
            catch (Exception ex)
            {
                Log.Warn(LogCategory.AppInstall, $"[pause-mirror] {module.Name}: threw, left unpatched: {ex.Message}");
            }
        }

        // PatchDll(string modulePath)
        public void PatchDll(string modulePath)
        {
            // Cecil resolves type references when it *serialises* the module — most
            // notably to find the underlying integer type of an enum used as a field
            // constant (MetadataBuilder.GetConstantType -> CheckedResolve). A user
            // assembly with a field constant typed as an enum from
            // Microsoft.Xna.Framework.Graphics (rescoped to FNA below) or from one of
            // our shims therefore forces an assembly resolve at Write time. The
            // default resolver only searches the module's own directory, so it can't
            // find FNA / the WPR shim assemblies and Write throws
            // AssemblyResolutionException — which the catch below used to swallow
            // silently, leaving that one DLL unpatched (its [System.Windows] typerefs
            // never redirected -> TypeLoadException at launch). Point the resolver at
            // the install dir *and* the running WPR bin (where FNA + the shims are
            // deployed) so those resolves succeed. Repro'd on "Beards and Beaks.dll".
            //
            // The WPR bin only helps a platform that HAS a WPR bin. On Android every
            // managed assembly is embedded in the APK and mapped out of it, so
            // AppContext.BaseDirectory holds no .dll at all and that resolve fails no
            // matter what is added here — which is why the same "Beards and Beaks.dll"
            // was still arriving unpatched on a phone long after the line above fixed
            // it on Windows. ConstantEnumStubResolver answers that one resolve without
            // needing a file; see its remarks.
            var resolver = new ConstantEnumStubResolver();
            resolver.AddSearchDirectory(Path.GetDirectoryName(modulePath)!);
            resolver.AddSearchDirectory(AppContext.BaseDirectory);

            string modulePathNameStandardized = Path.Combine(
                Path.GetDirectoryName(modulePath)!,
               AssemblyNameStandardization.Process(
                    Path.GetFileNameWithoutExtension(modulePath)) +
                Path.GetExtension(modulePath));

            // The pristine assembly is the INPUT, always. The first install renames the game's
            // own .dll to .dll.original and patches from it; every later call must read that
            // sidecar rather than the live, already-patched .dll — and must never overwrite it.
            // Until 2026-09-20 this method read modulePath unconditionally and then did
            // File.Move(modulePath, .original, overwrite: true), so any caller that did not
            // restore the sidecar first (the Android launcher's "PatchedVersion is behind"
            // repatch calls Patch() straight on the install folder) replaced the pristine
            // original with the previous patched output and patched THAT again. Seven version
            // bumps in, a phone's ".original" was the v27 output with every earlier pass baked
            // in, and the real original was gone — recoverable only by reinstalling the XAP.
            // Measured on the emulator: five of fifteen installs had a patched ".original".
            string originalPath = modulePathNameStandardized + ".original";
            bool hadOriginal = File.Exists(originalPath);
            string sourcePath = hadOriginal ? originalPath : modulePath;

            // ReadAssembly
            AssemblyDefinition assemblyData =
                Mono.Cecil.AssemblyDefinition.ReadAssembly(
                    sourcePath, new ReaderParameters { AssemblyResolver = resolver });

            Mono.Cecil.ModuleDefinition module = assemblyData.MainModule;

            if (hadOriginal && module.AssemblyReferences.Any(r =>
                    r.Name == "WPR.Framework.Xna" || r.Name == "WPR.Backend.FNA" ||
                    r.Name == "WPR.Framework.Silverlight"))
            {
                // The sidecar is itself a patched output (see above). Nothing here can undo
                // that; say so loudly, because the symptom downstream is a game that fails in
                // ways no current patcher version produces.
                Log.Warn(LogCategory.AppInstall,
                    $"[patch] {Path.GetFileName(originalPath)} is NOT a pristine original — it " +
                    "already references WPR assemblies, so an earlier repatch overwrote it with " +
                    "patched output. Patching it again stacks passes; reinstall this game from " +
                    "its XAP to recover the real original.");
            }

            assemblyData.Name.Name = AssemblyNameStandardization.Process(assemblyData.Name.Name);

            // Remove unneeded attribute (pretty sure!)
            foreach (var attrib in module.Assembly.CustomAttributes)
            {
                if (attrib.AttributeType.FullName ==
                    "System.Runtime.CompilerServices.CodeGenerationAttribute")
                {
                    module.Assembly.CustomAttributes.Remove(attrib);
                    break;
                }
            }

            /* Every assembly ref this loop is about to touch, keyed by the name it has BEFORE the
             * rename.
             *
             * A typeref in the TypeRef TABLE follows a rename for free — its Scope is that very
             * AssemblyNameReference instance, mutated in place. A type named inside a CUSTOM
             * ATTRIBUTE BLOB does not: the blob stores an assembly-qualified name as a STRING, and
             * when Cecil parses it (RescopeCustomAttributeTypeArguments, below) there is no ref
             * called "Microsoft.Xna.Framework" left to match — this loop renamed it to "FNA" — so
             * Cecil mints a fresh AssemblyNameReference for the dead WP7 identity.
             *
             * This map is how such a type gets back onto the same instance the table refs use. */
            Dictionary<string, AssemblyNameReference> assemblyScopesByOriginalName =
                new Dictionary<string, AssemblyNameReference>(StringComparer.Ordinal);

            // The module's reference to WPR.Framework.WinRT, found below or added on first use by
            // RescopeTypeReference (WinRtProjectionTypes). One instance, so no duplicate AssemblyRef.
            AssemblyNameReference? winRtScope = null;

            // module.AssemblyReferences cycle
            foreach (var refer in module.AssemblyReferences)
            {
                assemblyScopesByOriginalName[refer.Name] = refer;

                // A reference compiled against a .winmd carries ContentType=WindowsRuntime, and
                // .NET throws PlatformNotSupported for every method naming a type through it.
                // WinRtRefStripper clears it at install, but only on the live DLL: the patcher
                // reads the .original, so a strip that failed (or any repatch) left the flag in.
                // Clearing it here covers install, repatch and Android's launch-time repatch. v47.
                refer.IsWindowsRuntime = false;

                // Microsoft.Xna.* references are left as the game shipped them: WPR builds a facade
                // assembly of each name (Src/Facades/Xna) that forwards to the implementation.
                if (IsSilverlightSystemWindows(refer))
                {
                    // Version 61: Silverlight's System.Windows is the shim assembly. The shims
                    // declare Silverlight's own namespaces and names, so every type a game takes
                    // from it binds by its own name, and a shim added later binds with no table
                    // entry and no repatch. (.NET's System.Windows.dll is a different assembly,
                    // a 4.0.0.0 WPF-era facade; its token is b03f5f7f11d50a3a and it is left alone.)
                    refer.Name = SilverlightCompRef.Name;
                    refer.Version = SilverlightCompRef.Version;
                    refer.PublicKey = SilverlightCompRef.PublicKey;
                }
                else if (WinmdStubber.RetargetWinRtMscorlib(refer))
                {
                    // Version 43: a WinRT stub written before WinmdStubber retargeted this itself.
                }
                else if (refer.Name.Equals(WinRtProjectionAssembly, StringComparison.OrdinalIgnoreCase))
                {
                    // Version 45: WPR.Framework.WinRT is built as 4.0.0.0 with no strong name. WP8
                    // user IL names 4.0.0.0 with Microsoft's token, and WinmdStubber's stubs named
                    // 8.0.0.0; a request above what is loaded does not bind.
                    WinmdStubber.RetargetWinRtProjection(refer);
                    winRtScope = refer;
                }
                else if (refer.Name.Equals("mscorlib.Extensions",
                    StringComparison.OrdinalIgnoreCase))
                {
                    // Version 44: the runtime's mscorlib facade, not System.Runtime. WP7 keeps
                    // System.Threading.Mutex here, and System.Runtime does not forward it (it lives
                    // in System.Threading); the mscorlib facade forwards it, and every other type
                    // games take from mscorlib.Extensions resolves no worse. AE Fruit Slash's
                    // AEGame..ctor news a Mutex, so the game died before its first frame.
                    refer.Name = "mscorlib";
                    refer.Version = new Version(4, 0, 0, 0);
                    refer.PublicKey = null;
                    refer.PublicKeyToken = new byte[] { 0xb7, 0x7a, 0x5c, 0x56, 0x19, 0x34, 0xe0, 0x89 };
                }
                else if (refer.Name.Equals("System.ServiceModel",
                    StringComparison.OrdinalIgnoreCase))
                {
                    refer.Name = ServiceModelPrimitivesRef.Name;
                    refer.Version = ServiceModelPrimitivesRef.Version;
                    refer.PublicKey = ServiceModelPrimitivesRef.PublicKey;
                }
            }

            //RnD
            PatchRelaxedXmlNullableAttribTextSerialize(module);

            // Add AssemblyReferences
            module.AssemblyReferences.Add(FnaBackendRef);
            module.AssemblyReferences.Add(SilverlightCompRef);
            // Stage 5a: register the owned XNA value-type assembly so the per-typeref rescope below
            // (existingRef.Scope = WprFrameworkXnaRef) resolves. Without this, the scope is dangling
            // and Cecil defaults it to the game module itself → "Could not load type
            // 'Microsoft.Xna.Framework.Rectangle' from assembly '<game>'".
            module.AssemblyReferences.Add(WprFrameworkXnaRef);
            module.AssemblyReferences.Add(SystemRunTimeRef);
            module.AssemblyReferences.Add(ServiceModelPrimitivesRef);
            module.AssemblyReferences.Add(ServiceModelHTTPRef);
            module.AssemblyReferences.Add(JsonSerializationRef);
            module.AssemblyReferences.Add(ObjectModelRef);

            // create Ref. Patch Cache
            Dictionary<string, TypeReference> typeRefPatchCache
                = new Dictionary<string, TypeReference>();

            // module.GetMemberReferences cycle
            foreach (var memberRef in module.GetMemberReferences())
            {
                //if (memberRef.FullName.Contains("Collect"))
                //{
                //    Debug.WriteLine("[Collect] memberRef fullname: "
                //        + memberRef.FullName);
                //}

                foreach (var patch in MemberPatches)
                {
                    /*
                    if (memberRef.FullName.Contains("Collect"))
                    {
                        //Debug.WriteLine("[TeSTING] memberRef.FullName.Contains : Collect");
                        Debug.WriteLine("[TeSTING] memberRef.FullName Contains Collect: " 
                            + memberRef.FullName);
                    }
                    */

                    if (memberRef.FullName == patch.Key)
                    {
                        if (typeRefPatchCache.ContainsKey(patch.Value.FullName!))
                        {
                            memberRef.DeclaringType = typeRefPatchCache[patch.Value.FullName!];
                        }
                        else
                        {
                            memberRef.DeclaringType = module.ImportReference(patch.Value);
                            typeRefPatchCache.Add(patch.Value.FullName!, memberRef.DeclaringType);
                        }
                    }
                }
            }

            // cycle existing refs...
            int ownImplementationRefs = 0;
            foreach (var existingRef in module.GetTypeReferences())
            {
                RescopeTypeReference(existingRef);
            }//for...

            /* GetTypeReferences() above walks the TypeRef metadata TABLE, and a type named inside
             * a custom attribute blob is not in it — the blob carries an assembly-qualified name
             * as a plain string, which no amount of table walking reaches. Those strings need
             * exactly the same rescoping, so hand the same function over them. */
            RescopeCustomAttributeTypeArguments(module, RescopeTypeReference);

            if (ownImplementationRefs > 0)
            {
                Log.Info(LogCategory.AppInstall,
                    $"[own-impl] {Path.GetFileName(modulePath)}: {ownImplementationRefs} framework-named typeref(s) left on the package's own assembly");
            }

            // Points one typeref at whatever WPR assembly owns that type now. A local function so
            // the blob walk above and the table walk share one set of rules; they must agree, or a
            // typeof() in an attribute resolves somewhere its IL counterpart does not.
            void RescopeTypeReference(TypeReference existingRef)
            {
                existingRef.Name = AssemblyNameStandardization.Process(existingRef.Name);

                // Version 45: a framework-named type the PACKAGE itself implements stays the
                // package's. Every rule below matches by full name, whatever assembly the ref
                // points at, so Gravity Guy 2's SharpDXCommon.dll (its own managed
                // Microsoft.Xna.Framework.Game, Texture2D ... over SharpDX) was being swapped for
                // WPR.Framework.Xna's, and the game's code never ran. Only refs scoped to a package
                // assembly that is not itself a framework copy are kept (PackageOwnsScope).
                if (existingRef.Scope is AssemblyNameReference owner
                    && FrameworkNamespace.IsMatch(existingRef.Namespace ?? string.Empty)
                    && PackageOwnsScope(modulePath, owner.Name))
                {
                    ownImplementationRefs++;
                    return;
                }

                if (WinRtProjectionTypes.Contains(existingRef.FullName) || WinRtImplementedTypes.Contains(existingRef.FullName))
                {
                    if (winRtScope is null)
                    {
                        winRtScope = new AssemblyNameReference(WinRtProjectionAssembly, new Version(4, 0, 0, 0));
                        module.AssemblyReferences.Add(winRtScope);
                    }
                    existingRef.Scope = winRtScope;
                }
                else
                {
                    if (Patches.ContainsKey(existingRef.FullName))
                    {
                        TypePatchInfo patch = Patches[existingRef.FullName];
                        if (patch != null)
                        {
                            if (patch.NewName != null)
                            {
                                existingRef.Name = patch.NewName;
                            }

                            if (patch.NewNamespace != null)
                            {
                                existingRef.Namespace = patch.NewNamespace;
                            }

                            if (patch.Reference != null)
                            {
                                existingRef.Scope = patch.Reference;
                            }
                        }
                    }
                    else if (existingRef.Scope is AssemblyNameReference blobScope
                        && assemblyScopesByOriginalName.TryGetValue(
                            blobScope.Name, out AssemblyNameReference? liveScope)
                        && !ReferenceEquals(blobScope, liveScope))
                    {
                        /* Only a type parsed out of a custom attribute blob reaches this. A
                         * TypeRef-table entry already holds `liveScope` itself, so the identity
                         * test short-circuits and the table walk is bit-for-bit unchanged.
                         *
                         * A blob-parsed one holds a throwaway AssemblyNameReference Cecil built
                         * from the string, still naming the pre-rename identity. Retarget it, and
                         * every rename this method performs — Microsoft.Xna.* -> FNA,
                         * mscorlib.Extensions -> System.Runtime, System.ServiceModel ->
                         * System.ServiceModel.Primitives — carries over to attribute arguments for
                         * free, rather than each needing its own entry here. */
                        existingRef.Scope = liveScope;
                    }
                }
            }//RescopeTypeReference


            // Send every IsolatedStorageFile.OpenFile / CreateFile call through the sharing shim.
            // Must run after the reference tables, so the shim assembly is already referenced and
            // ImportReference reuses that ref instead of adding a second one.
            RedirectIsolatedStorageCalls(module);

            // Same shape for sockets: Silverlight's *Async(SocketAsyncEventArgs) always completed
            // asynchronously, .NET's may not.
            RedirectSocketCalls(module);

            // Module.Name of a stream-loaded assembly is "<Unknown>"; on the phone it was the file.
            RedirectModuleNameCalls(module);

            // Android cannot create a named Mutex; NamedMutex falls back to a process-local one.
            RedirectNamedMutexCalls(module);

            // Hand a game back the metadata tokens its own assembly shipped with. Same placement
            // reasoning as the call above: the shim assembly is already referenced by now. Must
            // run before the write, which is where Cecil renumbers the TypeDef table.
            PreserveOriginalMetadataTokens(module, Path.GetFileName(modulePath));

            // Same obfuscator, different defect: make its StackTrace caller-identity checks answer
            // "trusted". Independent of the pass above — that one repairs an input WPR itself
            // perturbs, this one a check WPR does not touch at all — so ordering between them does
            // not matter. Placed here because both concern Eazfuscator and read best together.
            NeutraliseObfuscatorStackIdentityChecks(module, Path.GetFileName(modulePath));

            // A MediaStateChanged handler that only mirrors an outside pause of the music (only the
            // game itself can pause it here). Pattern, not title: see the method.
            EmptyPauseMirroringMediaStateHandlers(module);

            // There are no per-game IL fixups: .claude/rules/no-per-game-rules.md.

            // Make blocks MonoVM refuses to import reachable only from somewhere it accepts.
            // Runs last of the IL passes: it reads the finished control flow, and it must see
            // any block the fixups above introduced.
            if (MonoRelocationDisabled)
            {
                Log.Info(LogCategory.AppInstall,
                    $"[mono-fixup] {Path.GetFileName(modulePath)}: RelocateMonoStackConflictBlocks " +
                    "SKIPPED (WPR_PATCHER_DISABLE_MONO_RELOCATION is set).");
            }
            else
            {
                RelocateMonoStackConflictBlocks(module, Path.GetFileName(modulePath));
            }

            // Cecil resolves a constant's declared type while building the Constant table,
            // to learn the integer behind an enum. Prepare an answer for any that cannot be
            // found on disk — which on Android is all of ours. Must run here: the scope Cecil
            // asks for is the one the rescoping above just renamed it to.
            int stubbed = resolver.PrimeConstantTypes(module);
            if (stubbed > 0)
            {
                Log.Info(LogCategory.AppInstall,
                    $"[const-fixup] {Path.GetFileName(modulePath)}: {stubbed} constant type(s) " +
                    "resolved from the constant's own value (assembly not on disk).");
            }

            // create .dll.new
            try
            {
                assemblyData.Write(modulePath + ".new");
            }
            catch (Exception ex)
            {
                // With the resolver supplied above this should be rare, but if a
                // constant still references a genuinely unresolvable assembly Cecil
                // throws here. Log it loudly — a bare Debug.WriteLine never reached
                // the install log, which is exactly how an unpatched DLL slipped
                // through unnoticed before. Leave the original in place.
                Log.Error(LogCategory.AppInstall,
                    $"Cecil failed to write patched assembly '{modulePath}'. It will be left UNPATCHED. Error:\n{ex}");

                assemblyData.Dispose();

                // Drop the truncated/empty .new so a stale 0-byte file can't linger
                // or be mistaken for a real patched output.
                try { File.Delete(modulePath + ".new"); } catch { /* best-effort */ }
                return;
            }

            assemblyData.Dispose();

            if (!hadOriginal)
            {
                // First patch of this assembly: the game's own .dll becomes the sidecar.
                // .dll -> .dll.original
                File.Move(modulePath, originalPath, true);
            }
            else if (!string.Equals(modulePath, modulePathNameStandardized, StringComparison.OrdinalIgnoreCase)
                     && File.Exists(modulePath))
            {
                // Repatch of a name-standardised assembly: the un-standardised live file is
                // superseded by the standardised one written below. The sidecar is untouched.
                File.Delete(modulePath);
            }

            // .dll.new - > .dll
            File.Move(modulePath + ".new", modulePathNameStandardized, true);
        }//PatchDll

        public void Patch(string appRootPath, Action<int> progress, CancellationToken token)
        {
            List<string> filenameList = Directory.EnumerateFiles(appRootPath,
                "*.dll", SearchOption.AllDirectories).ToList();
            int totalCount = filenameList.Count;
            int current = 0;

            foreach (var filename in filenameList)
            {
                if (token.IsCancellationRequested)
                {
                    return;
                }

                // A native DLL (a WP8 component, SharpDX's interop) has nothing to patch, and
                // handing it to Cecil only throws (see ManagedImage).
                if (!ManagedImage.IsManaged(filename))
                {
                    current++;
                    progress((int)(current * 100.0 / totalCount));
                    continue;
                }

                try
                {
                    PatchDll(filename);
                    Debug.WriteLine($"[i] Patching DLL with path: {filename}.\n");
                }
                catch (Exception ex)
                {
                    Log.Error(LogCategory.AppInstall, $"Fail to patch DLL with path: {filename}. Error:\n{ex}");
                    continue;
                }

                current++;
                progress((int)(current * 100.0 / totalCount));
            }

            // Post-patch cleanup: delete bundled DLLs that contribute nothing
            // useful after typeref retargeting. Real WP toolkits ship copies of
            // System.Windows.Interactivity / Microsoft.Expression.Interactions /
            // Microsoft.Advertising.Mobile that the user game references in
            // metadata but never actually exercises. Removing them eliminates
            // duplicate type definitions (a common source of cross-assembly
            // type-identity bugs — see GestureEventArgs) and shaves install
            // size. The cleanup is conservative: it removes a DLL only when
            // nothing else in the install dir (post-patch) still imports its
            // assembly name as a reference.
            try { CleanupUnreferencedBundledDlls(appRootPath); }
            catch (Exception ex)
            {
                Debug.WriteLine($"[i] CleanupUnreferencedBundledDlls failed: {ex.Message}");
            }
        }

        /// <summary>
        /// Iteratively remove DLLs that no longer have inbound references from any
        /// other DLL in <paramref name="appRootPath"/>. The user's main assembly
        /// (the entry point) is treated as a permanent root. We loop until no
        /// further DLLs can be pruned — handles transitive deletions
        /// (e.g. Interactions referenced only by Expression, both then go).
        /// </summary>
        private static void CleanupUnreferencedBundledDlls(string appRootPath)
        {
            // Names of DLLs we'd consider stripping (anything that isn't a WPR
            // shim and isn't the user's primary assembly. The user's assembly is
            // identified loosely: keep every DLL whose presence the patcher
            // explicitly touched at least once — for simplicity, "anything not
            // in the strip-candidate list below stays").
            // We start conservatively with a known-safe candidate set; can grow
            // it later as we shim more types.
            var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Microsoft.Expression.Interactions",
                "System.Windows.Interactivity",
            };

            // Loop: each pass removes any candidate whose name appears in no
            // remaining DLL's AssemblyReferences.
            bool changed;
            do
            {
                changed = false;
                // Managed ones only: Cecil throws on a native DLL (see ManagedImage).
                string[] dlls = Directory.GetFiles(appRootPath, "*.dll", SearchOption.AllDirectories)
                    .Where(ManagedImage.IsManaged)
                    .ToArray();

                // Build inbound-reference set: which assembly names are still imported by SOMEONE?
                var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (string dll in dlls)
                {
                    AssemblyDefinition? asm = null;
                    try { asm = AssemblyDefinition.ReadAssembly(dll); }
                    catch { continue; }
                    using (asm)
                    {
                        foreach (var r in asm.MainModule.AssemblyReferences)
                            referenced.Add(r.Name);
                    }
                }

                foreach (string dll in dlls)
                {
                    string name;
                    AssemblyDefinition? asm = null;
                    try { asm = AssemblyDefinition.ReadAssembly(dll); name = asm.Name.Name; }
                    catch { asm?.Dispose(); continue; }
                    asm.Dispose();

                    if (!candidates.Contains(name)) continue;
                    if (referenced.Contains(name)) continue; // someone still imports it; skip this pass

                    // Safe to remove: nobody imports this assembly anymore.
                    try
                    {
                        File.Delete(dll);
                        string original = dll + ".original";
                        if (File.Exists(original)) File.Delete(original);
                        Debug.WriteLine($"[i] Removed unreferenced bundled DLL: {Path.GetFileName(dll)}");
                        changed = true;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine($"[i] Couldn't delete '{dll}': {ex.Message}");
                    }
                }
            } while (changed);
        }
    }
}
