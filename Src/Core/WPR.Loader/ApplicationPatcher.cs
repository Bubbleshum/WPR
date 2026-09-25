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
        public static int Version => 38;

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

        private class TypePatchInfo
        {
            public String? NewName;
            public String? NewNamespace;
            public AssemblyNameReference? Reference;
        }

        private Dictionary<string, TypePatchInfo> Patches;
        private Dictionary<string, Type> MemberPatches;

        /// <summary>
        /// The XNA value/math types that were pulled OUT of FNA into the WPR-owned
        /// <c>WPR.Framework.Xna</c> assembly (Stage 5a). The per-typeref loop rescopes game
        /// references to these straight to <see cref="WprFrameworkXnaRef"/>, overriding the
        /// coarse <c>Microsoft.Xna.* -&gt; FNA</c> assembly-ref rename (value types share the
        /// one <c>Microsoft.Xna.Framework</c> ref with <c>GraphicsDevice</c> etc., so they
        /// can't be split at the assembly-ref level). Runtime types (Game, GraphicsDevice,
        /// SpriteBatch, …) still go to FNA. This list MUST equal WPR.Framework.Xna's public
        /// surface — keep it in sync if 5b/5c move more types out of FNA (it replaces the
        /// former FNA <c>WprXnaForwarders.cs</c> redirect: games now bind the owned assembly
        /// directly).
        /// </summary>
        private static readonly HashSet<string> WprFrameworkXnaTypes = new(StringComparer.Ordinal)
        {
            "Microsoft.Xna.Framework.Audio.AudioCategory",
            "Microsoft.Xna.Framework.Audio.AudioChannels",
            "Microsoft.Xna.Framework.Audio.AudioEmitter",
            "Microsoft.Xna.Framework.Audio.AudioEngine",
            "Microsoft.Xna.Framework.Audio.AudioListener",
            "Microsoft.Xna.Framework.Audio.AudioStopOptions",
            "Microsoft.Xna.Framework.Audio.Cue",
            "Microsoft.Xna.Framework.Audio.DynamicSoundEffectInstance",
            "Microsoft.Xna.Framework.Audio.InstancePlayLimitException",
            "Microsoft.Xna.Framework.Audio.Microphone",
            "Microsoft.Xna.Framework.Audio.MicrophoneState",
            "Microsoft.Xna.Framework.Audio.NoAudioHardwareException",
            "Microsoft.Xna.Framework.Audio.NoMicrophoneConnectedException",
            "Microsoft.Xna.Framework.Audio.RendererDetail",
            "Microsoft.Xna.Framework.Audio.SoundBank",
            "Microsoft.Xna.Framework.Audio.SoundEffect",
            "Microsoft.Xna.Framework.Audio.SoundEffectInstance",
            "Microsoft.Xna.Framework.Audio.SoundState",
            "Microsoft.Xna.Framework.Audio.WaveBank",
            "Microsoft.Xna.Framework.BoundingBox",
            "Microsoft.Xna.Framework.BoundingFrustum",
            "Microsoft.Xna.Framework.BoundingSphere",
            "Microsoft.Xna.Framework.Color",
            "Microsoft.Xna.Framework.ContainmentType",
            "Microsoft.Xna.Framework.Content.ContentLoadException",
            "Microsoft.Xna.Framework.Content.ContentManager",
            "Microsoft.Xna.Framework.Content.ContentReader",
            "Microsoft.Xna.Framework.Content.ContentSerializerAttribute",
            "Microsoft.Xna.Framework.Content.ContentSerializerCollectionItemNameAttribute",
            "Microsoft.Xna.Framework.Content.ContentSerializerIgnoreAttribute",
            "Microsoft.Xna.Framework.Content.ContentSerializerRuntimeTypeAttribute",
            "Microsoft.Xna.Framework.Content.ContentSerializerTypeVersionAttribute",
            "Microsoft.Xna.Framework.Content.ContentTypeReader",
            // Games that ship a custom ContentTypeReader subclass emit a typeref to the OPEN generic;
            // Cecil renders that FullName with the arity suffix, so it needs its own entry (same shape
            // as IPackedVector`1 below).
            "Microsoft.Xna.Framework.Content.ContentTypeReader`1",
            "Microsoft.Xna.Framework.Content.ContentTypeReaderManager",
            "Microsoft.Xna.Framework.Content.ResourceContentManager",
            "Microsoft.Xna.Framework.Curve",
            "Microsoft.Xna.Framework.CurveContinuity",
            "Microsoft.Xna.Framework.CurveKey",
            "Microsoft.Xna.Framework.CurveKeyCollection",
            "Microsoft.Xna.Framework.CurveLoopType",
            "Microsoft.Xna.Framework.CurveTangent",
            "Microsoft.Xna.Framework.Design.BoundingBoxConverter",
            "Microsoft.Xna.Framework.Design.BoundingSphereConverter",
            "Microsoft.Xna.Framework.Design.ColorConverter",
            "Microsoft.Xna.Framework.Design.MathTypeConverter",
            "Microsoft.Xna.Framework.Design.MatrixConverter",
            "Microsoft.Xna.Framework.Design.PlaneConverter",
            "Microsoft.Xna.Framework.Design.PointConverter",
            "Microsoft.Xna.Framework.Design.QuaternionConverter",
            "Microsoft.Xna.Framework.Design.RayConverter",
            "Microsoft.Xna.Framework.Design.RectangleConverter",
            "Microsoft.Xna.Framework.Design.Vector2Converter",
            "Microsoft.Xna.Framework.Design.Vector3Converter",
            "Microsoft.Xna.Framework.Design.Vector4Converter",
            "Microsoft.Xna.Framework.DisplayOrientation",
            "Microsoft.Xna.Framework.FrameworkDispatcher",
            "Microsoft.Xna.Framework.GameComponentCollection",
            "Microsoft.Xna.Framework.GameComponentCollectionEventArgs",
            // Spine relocation step 2 (2026-09-01, version 21). The XNA game-loop spine moved out
            // of the FNA backend into WPR.Framework.Xna, so games bind these five by WPR identity
            // rather than through FNA.
            //
            // GraphicsDeviceManager must NOT be added here, and the reason changed on 2026-09-02.
            // It used to be "the base class lives in FNA"; the base now lives in WPR.Framework.Xna
            // like the rest of the spine. What keeps it out is this set being tested BEFORE
            // `Patches`: adding it would silently win over the `Patches` entry pointing at
            // WPR.Backend.FNA.Compat.GraphicsDeviceManager, and games would bind the plain base
            // instead of the WP7 override — losing the 800x480 clamp and the orientation request,
            // with a clean build and no error anywhere.
            "Microsoft.Xna.Framework.DrawableGameComponent",
            "Microsoft.Xna.Framework.Game",
            "Microsoft.Xna.Framework.GameComponent",
            "Microsoft.Xna.Framework.GameServiceContainer",
            "Microsoft.Xna.Framework.GameWindow",
            "Microsoft.Xna.Framework.GraphicsDeviceInformation",
            "Microsoft.Xna.Framework.PreparingDeviceSettingsEventArgs",
            "Microsoft.Xna.Framework.GameTime",
            // The WP7.1 Silverlight/XNA mixed-mode surface. GameTimer and GameTimerEventArgs came
            // from Microsoft.Xna.Framework; SharedGraphicsDeviceManager and
            // Graphics.GraphicsDeviceExtensions came from a SEPARATE assembly,
            // Microsoft.Xna.Framework.Interop, for which WPR has no counterpart assembly.
            //
            // That needed no change in RescopeAssemblyReferences: its `Contains("Microsoft.Xna")`
            // branch already renames the Interop ref to FNA along with every other XNA ref, and
            // this set is tested per TYPEREF and by FullName, so it overrides that rename for
            // exactly these four names — the same split the value types rely on. (v30.)
            "Microsoft.Xna.Framework.GameTimer",
            "Microsoft.Xna.Framework.GameTimerEventArgs",
            "Microsoft.Xna.Framework.SharedGraphicsDeviceManager",
            "Microsoft.Xna.Framework.Graphics.GraphicsDeviceExtensions",
            "Microsoft.Xna.Framework.Graphics.AlphaTestEffect",
            "Microsoft.Xna.Framework.Graphics.BasicEffect",
            "Microsoft.Xna.Framework.Graphics.Blend",
            "Microsoft.Xna.Framework.Graphics.BlendFunction",
            "Microsoft.Xna.Framework.Graphics.BlendState",
            "Microsoft.Xna.Framework.Graphics.BufferUsage",
            "Microsoft.Xna.Framework.Graphics.ClearOptions",
            "Microsoft.Xna.Framework.Graphics.ColorWriteChannels",
            "Microsoft.Xna.Framework.Graphics.CompareFunction",
            "Microsoft.Xna.Framework.Graphics.CubeMapFace",
            "Microsoft.Xna.Framework.Graphics.CullMode",
            "Microsoft.Xna.Framework.Graphics.DepthFormat",
            "Microsoft.Xna.Framework.Graphics.DepthStencilState",
            "Microsoft.Xna.Framework.Graphics.DeviceLostException",
            "Microsoft.Xna.Framework.Graphics.DeviceNotResetException",
            "Microsoft.Xna.Framework.Graphics.DirectionalLight",
            "Microsoft.Xna.Framework.Graphics.DisplayMode",
            "Microsoft.Xna.Framework.Graphics.DisplayModeCollection",
            "Microsoft.Xna.Framework.Graphics.DualTextureEffect",
            "Microsoft.Xna.Framework.Graphics.DynamicIndexBuffer",
            "Microsoft.Xna.Framework.Graphics.DynamicVertexBuffer",
            "Microsoft.Xna.Framework.Graphics.Effect",
            "Microsoft.Xna.Framework.Graphics.EffectAnnotation",
            "Microsoft.Xna.Framework.Graphics.EffectAnnotationCollection",
            "Microsoft.Xna.Framework.Graphics.EffectMaterial",
            "Microsoft.Xna.Framework.Graphics.EffectParameter",
            "Microsoft.Xna.Framework.Graphics.EffectParameterClass",
            "Microsoft.Xna.Framework.Graphics.EffectParameterCollection",
            "Microsoft.Xna.Framework.Graphics.EffectParameterType",
            "Microsoft.Xna.Framework.Graphics.EffectPass",
            "Microsoft.Xna.Framework.Graphics.EffectPassCollection",
            "Microsoft.Xna.Framework.Graphics.EffectTechnique",
            "Microsoft.Xna.Framework.Graphics.EffectTechniqueCollection",
            "Microsoft.Xna.Framework.Graphics.EnvironmentMapEffect",
            "Microsoft.Xna.Framework.Graphics.FillMode",
            "Microsoft.Xna.Framework.Graphics.GraphicsAdapter",
            "Microsoft.Xna.Framework.Graphics.GraphicsDevice",
            "Microsoft.Xna.Framework.Graphics.GraphicsDeviceStatus",
            "Microsoft.Xna.Framework.Graphics.GraphicsProfile",
            "Microsoft.Xna.Framework.Graphics.GraphicsResource",
            "Microsoft.Xna.Framework.Graphics.IEffectFog",
            "Microsoft.Xna.Framework.Graphics.IEffectLights",
            "Microsoft.Xna.Framework.Graphics.IEffectMatrices",
            "Microsoft.Xna.Framework.Graphics.IGraphicsDeviceService",
            "Microsoft.Xna.Framework.Graphics.IVertexType",
            "Microsoft.Xna.Framework.Graphics.IndexBuffer",
            "Microsoft.Xna.Framework.Graphics.IndexElementSize",
            "Microsoft.Xna.Framework.Graphics.Model",
            "Microsoft.Xna.Framework.Graphics.ModelBone",
            "Microsoft.Xna.Framework.Graphics.ModelBoneCollection",
            "Microsoft.Xna.Framework.Graphics.ModelBoneCollection/Enumerator",
            "Microsoft.Xna.Framework.Graphics.ModelEffectCollection",
            "Microsoft.Xna.Framework.Graphics.ModelEffectCollection/Enumerator",
            "Microsoft.Xna.Framework.Graphics.ModelMesh",
            "Microsoft.Xna.Framework.Graphics.ModelMeshCollection",
            "Microsoft.Xna.Framework.Graphics.ModelMeshCollection/Enumerator",
            "Microsoft.Xna.Framework.Graphics.ModelMeshPart",
            "Microsoft.Xna.Framework.Graphics.ModelMeshPartCollection",
            "Microsoft.Xna.Framework.Graphics.ModelMeshPartCollection/Enumerator",
            "Microsoft.Xna.Framework.Graphics.NoSuitableGraphicsDeviceException",
            "Microsoft.Xna.Framework.Graphics.OcclusionQuery",
            "Microsoft.Xna.Framework.Graphics.PackedVector.Alpha8",
            "Microsoft.Xna.Framework.Graphics.PackedVector.Bgr565",
            "Microsoft.Xna.Framework.Graphics.PackedVector.Bgra4444",
            "Microsoft.Xna.Framework.Graphics.PackedVector.Bgra5551",
            "Microsoft.Xna.Framework.Graphics.PackedVector.Byte4",
            "Microsoft.Xna.Framework.Graphics.PackedVector.HalfSingle",
            "Microsoft.Xna.Framework.Graphics.PackedVector.HalfVector2",
            "Microsoft.Xna.Framework.Graphics.PackedVector.HalfVector4",
            "Microsoft.Xna.Framework.Graphics.PackedVector.IPackedVector",
            "Microsoft.Xna.Framework.Graphics.PackedVector.IPackedVector`1",
            "Microsoft.Xna.Framework.Graphics.PackedVector.NormalizedByte2",
            "Microsoft.Xna.Framework.Graphics.PackedVector.NormalizedByte4",
            "Microsoft.Xna.Framework.Graphics.PackedVector.NormalizedShort2",
            "Microsoft.Xna.Framework.Graphics.PackedVector.NormalizedShort4",
            "Microsoft.Xna.Framework.Graphics.PackedVector.Rg32",
            "Microsoft.Xna.Framework.Graphics.PackedVector.Rgba1010102",
            "Microsoft.Xna.Framework.Graphics.PackedVector.Rgba64",
            "Microsoft.Xna.Framework.Graphics.PackedVector.Short2",
            "Microsoft.Xna.Framework.Graphics.PackedVector.Short4",
            "Microsoft.Xna.Framework.Graphics.PresentInterval",
            "Microsoft.Xna.Framework.Graphics.PresentationParameters",
            "Microsoft.Xna.Framework.Graphics.PrimitiveType",
            "Microsoft.Xna.Framework.Graphics.RasterizerState",
            "Microsoft.Xna.Framework.Graphics.RenderTarget2D",
            "Microsoft.Xna.Framework.Graphics.RenderTargetBinding",
            "Microsoft.Xna.Framework.Graphics.RenderTargetCube",
            "Microsoft.Xna.Framework.Graphics.RenderTargetUsage",
            "Microsoft.Xna.Framework.Graphics.ResourceCreatedEventArgs",
            "Microsoft.Xna.Framework.Graphics.ResourceDestroyedEventArgs",
            "Microsoft.Xna.Framework.Graphics.SamplerState",
            "Microsoft.Xna.Framework.Graphics.SamplerStateCollection",
            "Microsoft.Xna.Framework.Graphics.SetDataOptions",
            "Microsoft.Xna.Framework.Graphics.SkinnedEffect",
            "Microsoft.Xna.Framework.Graphics.SpriteBatch",
            "Microsoft.Xna.Framework.Graphics.SpriteEffects",
            "Microsoft.Xna.Framework.Graphics.SpriteFont",
            "Microsoft.Xna.Framework.Graphics.SpriteSortMode",
            "Microsoft.Xna.Framework.Graphics.StencilOperation",
            "Microsoft.Xna.Framework.Graphics.SurfaceFormat",
            "Microsoft.Xna.Framework.Graphics.Texture",
            "Microsoft.Xna.Framework.Graphics.Texture2D",
            "Microsoft.Xna.Framework.Graphics.Texture3D",
            "Microsoft.Xna.Framework.Graphics.TextureAddressMode",
            "Microsoft.Xna.Framework.Graphics.TextureCollection",
            "Microsoft.Xna.Framework.Graphics.TextureCube",
            "Microsoft.Xna.Framework.Graphics.TextureFilter",
            "Microsoft.Xna.Framework.Graphics.VertexBuffer",
            "Microsoft.Xna.Framework.Graphics.VertexBufferBinding",
            "Microsoft.Xna.Framework.Graphics.VertexDeclaration",
            "Microsoft.Xna.Framework.Graphics.VertexElement",
            "Microsoft.Xna.Framework.Graphics.VertexElementFormat",
            "Microsoft.Xna.Framework.Graphics.VertexElementUsage",
            "Microsoft.Xna.Framework.Graphics.VertexPositionColor",
            "Microsoft.Xna.Framework.Graphics.VertexPositionColorTexture",
            "Microsoft.Xna.Framework.Graphics.VertexPositionNormalTexture",
            "Microsoft.Xna.Framework.Graphics.VertexPositionTexture",
            "Microsoft.Xna.Framework.Graphics.Viewport",
            "Microsoft.Xna.Framework.IDrawable",
            "Microsoft.Xna.Framework.IGameComponent",
            "Microsoft.Xna.Framework.IGraphicsDeviceManager",
            "Microsoft.Xna.Framework.IUpdateable",
            "Microsoft.Xna.Framework.Input.ButtonState",
            "Microsoft.Xna.Framework.Input.Buttons",
            "Microsoft.Xna.Framework.Input.GamePad",
            "Microsoft.Xna.Framework.Input.GamePadButtons",
            "Microsoft.Xna.Framework.Input.GamePadCapabilities",
            "Microsoft.Xna.Framework.Input.GamePadDPad",
            "Microsoft.Xna.Framework.Input.GamePadDeadZone",
            "Microsoft.Xna.Framework.Input.GamePadState",
            "Microsoft.Xna.Framework.Input.GamePadThumbSticks",
            "Microsoft.Xna.Framework.Input.GamePadTriggers",
            "Microsoft.Xna.Framework.Input.GamePadType",
            "Microsoft.Xna.Framework.Input.KeyState",
            "Microsoft.Xna.Framework.Input.Keyboard",
            "Microsoft.Xna.Framework.Input.KeyboardState",
            "Microsoft.Xna.Framework.Input.Keys",
            "Microsoft.Xna.Framework.Input.Mouse",
            "Microsoft.Xna.Framework.Input.MouseState",
            "Microsoft.Xna.Framework.Input.TextInputEXT",
            "Microsoft.Xna.Framework.Input.Touch.GestureSample",
            "Microsoft.Xna.Framework.Input.Touch.GestureType",
            "Microsoft.Xna.Framework.Input.Touch.TouchCollection",
            "Microsoft.Xna.Framework.Input.Touch.TouchLocation",
            "Microsoft.Xna.Framework.Input.Touch.TouchLocationState",
            "Microsoft.Xna.Framework.Input.Touch.TouchPanel",
            "Microsoft.Xna.Framework.Input.Touch.TouchPanelCapabilities",
            "Microsoft.Xna.Framework.LaunchParameters",
            "Microsoft.Xna.Framework.MathHelper",
            "Microsoft.Xna.Framework.Matrix",
            "Microsoft.Xna.Framework.Media.Album",
            "Microsoft.Xna.Framework.Media.AlbumCollection",
            "Microsoft.Xna.Framework.Media.Artist",
            "Microsoft.Xna.Framework.Media.ArtistCollection",
            "Microsoft.Xna.Framework.Media.Genre",
            "Microsoft.Xna.Framework.Media.MediaLibrary",
            "Microsoft.Xna.Framework.Media.MediaPlayer",
            "Microsoft.Xna.Framework.Media.MediaQueue",
            "Microsoft.Xna.Framework.Media.MediaSource",
            "Microsoft.Xna.Framework.Media.MediaSourceType",
            "Microsoft.Xna.Framework.Media.MediaState",
            "Microsoft.Xna.Framework.Media.Picture",
            "Microsoft.Xna.Framework.Media.PictureCollection",
            "Microsoft.Xna.Framework.Media.Playlist",
            "Microsoft.Xna.Framework.Media.PlaylistCollection",
            "Microsoft.Xna.Framework.Media.Song",
            "Microsoft.Xna.Framework.Media.SongCollection",
            "Microsoft.Xna.Framework.Media.Video",
            "Microsoft.Xna.Framework.Media.VideoPlayer",
            "Microsoft.Xna.Framework.Media.VideoSoundtrackType",
            "Microsoft.Xna.Framework.Media.VisualizationData",
            "Microsoft.Xna.Framework.Plane",
            "Microsoft.Xna.Framework.PlaneIntersectionType",
            "Microsoft.Xna.Framework.PlayerIndex",
            "Microsoft.Xna.Framework.Point",
            "Microsoft.Xna.Framework.Quaternion",
            "Microsoft.Xna.Framework.Ray",
            "Microsoft.Xna.Framework.Rectangle",
            "Microsoft.Xna.Framework.Storage.StorageContainer",
            "Microsoft.Xna.Framework.Storage.StorageDevice",
            "Microsoft.Xna.Framework.Storage.StorageDeviceNotConnectedException",
            "Microsoft.Xna.Framework.TitleContainer",
            "Microsoft.Xna.Framework.Vector2",
            "Microsoft.Xna.Framework.Vector3",
            "Microsoft.Xna.Framework.Vector4",
            "Microsoft.Xna.Framework.WprDebugTrace",
        };

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

            // (There is no longer a dedicated GamerServices assembly ref. Version 19 dissolved
            //  Microsoft.Xna.Framework.GamerServices into WPR.Framework.Xna, so the whole surface
            //  now rides WprFrameworkXnaRef — except GamerServicesComponent, which derives from
            //  FNA's GameComponent and is rescoped to FnaBackendRef.)


            // *** Patches ***
            Patches = new Dictionary<string, TypePatchInfo>()
            {
                { "System.Diagnostics.Stopwatch", new TypePatchInfo()
                {
                    Reference = SystemRunTimeRef
                }
                },
                { "Microsoft.Xna.Framework.GraphicsDeviceManager", new TypePatchInfo()
                {
                    NewName = "GraphicsDeviceManager",
                    NewNamespace = "WPR.Backend.FNA.Compat",
                    Reference = FnaBackendRef
                }
                },
                { "System.Windows.Application", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.WindowsCompability"
                }
                },
                { "System.Windows.ApplicationUnhandledExceptionEventArgs", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.WindowsCompability"
                }
                },
                // Avatar-award extension methods. The assembly-ref loop below captures the
                // Microsoft.Xna.Framework.GamerServicesExtensions reference but deliberately does
                // NOT rename it (renaming would collide with the plain GamerServices ref when a
                // game carries both), and the only typeref it rescopes by hand is
                // GamerServicesComponent. So every other type from that assembly needs an entry
                // here. Crimson Dragon: Side Story reaches this from MyGamerService.
                { "Microsoft.Xna.Framework.GamerServices.SignedInGamerExtensions", new TypePatchInfo()
                {
                    Reference = WprFrameworkXnaRef
                }
                },
                // Silverlight's HTTP-stack selector. Games call
                // WebRequest.RegisterPrefix("http://", WebRequestCreator.ClientHttp) while
                // setting up networking, often from a licence/trial check on the startup path —
                // Crimson Dragon: Side Story does it in Microsoft.Phone.Marketplace.HttpRequest's
                // ctor, so leaving this unpatched is a TypeLoadException before first frame.
                { "System.Net.Browser.WebRequestCreator", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.IO.IsolatedStorage.IsolatedStorageSettings", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewName="IsolatedStorageSettings2", //RnD
                    NewNamespace = "WPR.WindowsCompability"
                }
                },
                // A single-child container that scales its content to its slot. WP7 designs use it
                // to make one layout work at more than one resolution, and a page that names it in
                // XAML also carries an x:Name'd field for it in its generated InitializeComponent —
                // so a missing type is a TypeLoadException when that method is compiled, taking the
                // whole page rather than one container. Carcassonne's main menu names it 26 times.
                { "System.Windows.Controls.Viewbox", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Media.SolidColorBrush", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Media.Color", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Media.Colors", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Media.Brush", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Media.ImageBrush", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Media.ImageSource", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Media.Animation.Timeline", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Media.Animation.Storyboard", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Media.Animation.TimelineCollection", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Media.Animation.DoubleAnimation", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Media.Animation.DoubleAnimationUsingKeyFrames", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Media.Animation.DoubleKeyFrame", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Media.Animation.EasingDoubleKeyFrame", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Media.Animation.LinearDoubleKeyFrame", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Media.Animation.DoubleKeyFrameCollection", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Media.Animation.KeyTime", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Media.Animation.KeyTimeType", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Media.Animation.RepeatBehavior", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Media.Animation.ClockState", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Media.Animation.FillBehavior", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Media.Animation.IEasingFunction", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                // NOTE: the WP control/shell types that used to be redirected here
                // (Microsoft.Phone.Controls.GestureService / GestureListener / *GestureEventArgs,
                // PhoneApplicationFrame, PhoneApplicationPage, and every Microsoft.Phone.Shell.*
                // lifecycle type) now live in the Microsoft.Phone assembly under their real
                // namespaces, so user IL binds them natively — no patch entry needed.

                // UriMapperBase / UriMapper / UriMapping moved the OTHER way: out of the
                // Microsoft.Phone facade into WPR.SilverlightCompability, so the SL Frame's
                // UriMapper property can reference UriMapperBase without SL depending on the
                // Microsoft.Phone assembly (which now references SL). They keep their real
                // System.Windows.Navigation namespace, so only the assembly scope is retargeted
                // (like the gesture types used to be).
                { "System.Windows.Navigation.UriMapperBase", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                }
                },
                { "System.Windows.Navigation.UriMapper", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                }
                },
                { "System.Windows.Navigation.UriMapping", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                }
                },
                // Toolkit gesture types: on real WP7 these ship in
                // Microsoft.Phone.Controls.Toolkit.dll (namespace Microsoft.Phone.Controls), so
                // user IL references them from THAT assembly — unlike PhoneApplicationPage/Shell
                // (canonically in Microsoft.Phone.dll, which now resolve natively), the gesture
                // typerefs would otherwise bind the user-bundled toolkit dll. Retarget the
                // assembly scope to our Microsoft.Phone shim (namespace unchanged).
                { "Microsoft.Phone.Controls.GestureService", new TypePatchInfo()
                {
                    Reference = MicrosoftPhoneRef,
                }
                },
                { "Microsoft.Phone.Controls.GestureListener", new TypePatchInfo()
                {
                    Reference = MicrosoftPhoneRef,
                }
                },
                { "Microsoft.Phone.Controls.GestureEventArgs", new TypePatchInfo()
                {
                    Reference = MicrosoftPhoneRef,
                }
                },
                { "Microsoft.Phone.Controls.FlickGestureEventArgs", new TypePatchInfo()
                {
                    Reference = MicrosoftPhoneRef,
                }
                },
                { "Microsoft.Phone.Controls.DragStartedGestureEventArgs", new TypePatchInfo()
                {
                    Reference = MicrosoftPhoneRef,
                }
                },
                { "Microsoft.Phone.Controls.DragDeltaGestureEventArgs", new TypePatchInfo()
                {
                    Reference = MicrosoftPhoneRef,
                }
                },
                { "Microsoft.Phone.Controls.DragCompletedGestureEventArgs", new TypePatchInfo()
                {
                    Reference = MicrosoftPhoneRef,
                }
                },
                { "System.Windows.Controls.Frame", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Navigation.NavigationService", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Navigation.NavigationMode", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Navigation.NavigationEventArgs", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Navigation.NavigatingCancelEventArgs", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Navigation.NavigationFailedEventArgs", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Navigation.NavigatedEventHandler", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Navigation.NavigatingCancelEventHandler", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Navigation.NavigationFailedEventHandler", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Navigation.NavigationStoppedEventHandler", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Navigation.JournalEntry", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Markup.XmlLanguage", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Net.HttpUtility", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.FlowDirection", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Markup.XamlReader", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Markup.XamlParseException", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Markup.ContentPropertyAttribute", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Controls.Panel", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Controls.StackPanel", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Controls.Orientation", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Controls.UIElementCollection", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Controls.Grid", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Controls.DrawingSurfaceBackgroundGrid", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Controls.ScrollViewer", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Controls.ScrollBarVisibility", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Controls.TextBox", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Controls.Control", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Controls.TextChangedEventArgs", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Input.Touch", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Input.TouchFrameEventHandler", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Input.TouchFrameEventArgs", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Input.TouchPoint", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Input.TouchPointCollection", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Input.TouchDevice", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Input.TouchAction", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Controls.ColumnDefinition", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Controls.RowDefinition", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Controls.ColumnDefinitionCollection", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Controls.RowDefinitionCollection", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Controls.TextBlock", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.TextAlignment", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.TextWrapping", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Controls.Canvas", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                // Mixed-mode titles use MediaElement for their cutscenes — it is the ONLY element
                // in the page XAML of most of them. The shim plays nothing and reports that it
                // finished; see its class remarks for why that is the right degradation. (v30.)
                { "System.Windows.Controls.MediaElement", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Media.MediaElementState", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Media.TimelineMarker", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Media.TimelineMarkerCollection", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Media.TimelineMarkerRoutedEventArgs", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Media.TimelineMarkerRoutedEventHandler", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                // Silverlight application lifecycle, reached by mixed-mode titles that do their
                // setup from Application.Startup rather than WP7's Launching, or that put a
                // service of their own in <Application.ApplicationLifetimeObjects>. Little
                // Acorns needs the first pair, Galactic Reign the second. (v30.)
                { "System.Windows.StartupEventArgs", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.WindowsCompability"
                }
                },
                { "System.Windows.StartupEventHandler", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.WindowsCompability"
                }
                },
                { "System.Windows.IApplicationService", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.IApplicationLifetimeAware", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.ApplicationServiceContext", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Navigation.NavigationContext", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Navigation.LoadCompletedEventHandler", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                // UIElementRenderer keeps its Microsoft.Xna.Framework.Graphics namespace and only
                // moves ASSEMBLY, which is why it is here and not in WprFrameworkXnaTypes: it
                // names both Texture2D and UIElement, and Silverlight -> Xna is the direction that
                // reference already runs. Without the entry the blanket Microsoft.Xna.* -> FNA
                // rename resolves it to nothing. (v30.)
                { "Microsoft.Xna.Framework.Graphics.UIElementRenderer", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef
                }
                },
                { "System.Windows.Controls.Primitives.Popup", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Controls.UserControl", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Controls.ContentControl", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Controls.Button", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Controls.ItemsControl", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Controls.Border", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Shapes.Shape", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Shapes.Rectangle", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.StyleTypedPropertyAttribute", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.TemplatePartAttribute", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.TemplateVisualStateAttribute", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Markup.XmlnsDefinitionAttribute", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability.Markup"
                }
                },
                { "System.Windows.Markup.XmlnsPrefixAttribute", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability.Markup"
                }
                },
                { "System.Windows.VisualState", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.VisualStateGroup", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.VisualStateManager", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.VisualTransition", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.VisualStateChangedEventArgs", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.SizeChangedEventHandler", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.SizeChangedEventArgs", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Media.CompositionTarget", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.ComponentModel.DesignerProperties", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                // Bulk: input
                { "System.Windows.Input.ManipulationStartedEventArgs", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Input.ManipulationDeltaEventArgs", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Input.ManipulationCompletedEventArgs", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Input.ManipulationDelta", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Input.ManipulationVelocities", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Input.MouseButtonEventArgs", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Input.KeyEventArgs", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Input.Key", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                // Bulk: media — transforms
                { "System.Windows.Media.GeneralTransform", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Media.Transform", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Media.TransformCollection", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Media.TransformGroup", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Media.TranslateTransform", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Media.CompositeTransform", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                // The two most-used transforms, and the two that were missing. A transform type
                // with no entry keeps its WP7 scope, so the whole method naming it fails to
                // compile — Galactic Reign's ArmadaClient.MenuPage died on RotateTransform before
                // the game reached a menu. Scale is added beside it because a design that rotates
                // usually scales too.
                { "System.Windows.Media.RotateTransform", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Media.ScaleTransform", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Media.Projection", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Media.PlaneProjection", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                // Bulk: media — misc
                { "System.Windows.Media.Geometry", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Media.RectangleGeometry", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Media.GradientBrush", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Media.TileBrush", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Media.AlignmentX", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Media.AlignmentY", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Media.CacheMode", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Media.BitmapCache", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Media.VisualTreeHelper", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Media.Imaging.BitmapCreateOptions", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                // Bulk: animation easing
                { "System.Windows.Media.Animation.ExponentialEase", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Media.Animation.QuarticEase", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Media.Animation.EasingMode", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                // The rest of Silverlight's easing set plus its base, and the object-valued key
                // frames. A storyboard that names one of these is built when its PAGE is
                // constructed, so a missing easing function does not cost an animation — it costs
                // the whole page, and with it the game. Carcassonne needs Quintic/Quadratic/Circle
                // and the base type. (v30.)
                { "System.Windows.Media.Animation.EasingFunctionBase", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Media.Animation.QuinticEase", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Media.Animation.QuadraticEase", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Media.Animation.CubicEase", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Media.Animation.CircleEase", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Media.Animation.SineEase", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Media.Animation.BackEase", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Media.Animation.BounceEase", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Media.Animation.ElasticEase", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Media.Animation.PowerEase", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Media.Animation.ObjectAnimationUsingKeyFrames", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Media.Animation.ObjectKeyFrame", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Media.Animation.DiscreteObjectKeyFrame", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Media.Animation.ObjectKeyFrameCollection", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                // Bulk: controls
                { "System.Windows.Controls.CheckBox", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Controls.Primitives.ToggleButton", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Controls.Primitives.Selector", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                // RangeBase goes with ProgressBar: Silverlight declares Value/Minimum/Maximum
                // there, so a game setting one names RangeBase in its IL even though it is
                // holding a ProgressBar.
                { "System.Windows.Controls.Primitives.RangeBase", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },

                // ButtonBase goes with Button for the same reason RangeBase goes with ProgressBar:
                // Silverlight declares Click there, so `button.Click += h` compiles to
                // ButtonBase::add_Click and a game naming it needs the type to exist. RadioButton
                // completes the family (ButtonBase -> ToggleButton -> CheckBox/RadioButton).
                { "System.Windows.Controls.Primitives.ButtonBase", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Controls.RadioButton", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Controls.ProgressBar", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Controls.Page", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Controls.ContentPresenter", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Controls.ItemsPresenter", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Controls.ItemCollection", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Controls.ItemContainerGenerator", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                // Bulk: data binding helpers
                { "System.Windows.Data.IValueConverter", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Data.BindingExpressionBase", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Data.RelativeSource", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Data.RelativeSourceMode", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                // Bulk: fonts
                { "System.Windows.FontWeight", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.FontWeights", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                // Bulk: misc top-level
                { "System.Windows.Style", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.PropertyPath", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.Deployment", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                { "System.Windows.PresentationFrameworkCollection`1", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability" } },
                // Bulk: threading
                { "System.Windows.Threading.Dispatcher", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability.Threading" } },
                { "System.Windows.Threading.DispatcherOperation", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability.Threading" } },
                { "System.Windows.Threading.DispatcherTimer", new TypePatchInfo() { Reference = SilverlightCompRef, NewNamespace = "WPR.SilverlightCompability.Threading" } },
                { "System.Windows.Controls.ListBox", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Controls.SelectionMode", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Controls.SelectionChangedEventArgs", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Controls.SelectionChangedEventHandler", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.RoutedEventArgs", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.RoutedEventHandler", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.ExceptionRoutedEventArgs", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Controls.Image", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Media.Stretch", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Data.Binding", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Data.BindingMode", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.DataTemplate", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Resources.StreamResourceInfo", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.WindowsCompability"
                }
                },
                { "System.Windows.Interop.SilverlightHost", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.WindowsCompability"
                }
                },
                { "System.Windows.Interop.Content", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewName = "SilverlightHostContent",
                    NewNamespace = "WPR.WindowsCompability"
                }
                },
                { "System.Windows.Interop.Settings", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewName = "SilverlightHostSettings",
                    NewNamespace = "WPR.WindowsCompability"
                }
                },
                { "System.Windows.Thickness", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.DependencyObject", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.DependencyProperty", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.PropertyMetadata", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.PropertyChangedCallback", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.DependencyPropertyChangedEventArgs", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.UIElement", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.FrameworkElement", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Visibility", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.HorizontalAlignment", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.VerticalAlignment", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.GridLength", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.GridUnitType", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.CornerRadius", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Size", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Point", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Rect", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.Duration", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.SilverlightCompability"
                }
                },
                { "System.Windows.ResourceDictionary", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.WindowsCompability"
                }
                },
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
                //!
                { "System.Security.Cryptography.ProtectedData", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    //RnD : if uncomment it, WPR.WindowsCompabilityProtectedData class will be used
                    NewName = "ProtectedData",
                    NewNamespace = "WPR.WindowsCompability"
                }
                },
                //!
                { "System.Windows.Media.Imaging.BitmapImage", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewName = "BitmapImage",//RnD
                    NewNamespace = "WPR.WindowsCompability"
                }
                },
                //!
                { "System.Windows.Media.Imaging.WriteableBitmap", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.WindowsCompability"
                }
                },
                 //!
                { "System.Windows.Media.Imaging.BitmapSource", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.WindowsCompability"
                }
                },
                { "System.Windows.MessageBox", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.WindowsCompability"
                }
                },
                { "System.Windows.MessageBoxResult", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.WindowsCompability"
                }
                },
                { "System.Windows.MessageBoxButton", new TypePatchInfo()
                {
                    Reference = SilverlightCompRef,
                    NewNamespace = "WPR.WindowsCompability"
                }
                }
            };

            // *** Member Patches ***
            MemberPatches = new Dictionary<string, Type>
            {

                // RnD ***************************************
                //{
                //    "Microsoft.Xna.Framework.GamerServices.LeaderboardReader Microsoft.Xna.Framework.GamerServices.LeaderboardReader::Read(Microsoft.Xna.Framework.GamerServices.LeaderboardIdentity, Microsoft.Xna.Framework.GamerServices.Gamer, Int32)",
                //    typeof(Microsoft.Xna.Framework.GamerServices2.LeaderboardReader)
                //},
                // *******************************************
                {
                    "System.Boolean System.IO.IsolatedStorage.IsolatedStorageSettings::TryGetValue(System.String, ByRef)",
                    typeof(WPR.WindowsCompability.IsolatedStorageSettings2)
                },
                {
                    "System.IO.IsolatedStorage.IsolatedStorageSettings System.IO.IsolatedStorage.IsolatedStorageSettings::get_ApplicationSettings()",
                    typeof(WPR.WindowsCompability.IsolatedStorageSettings2)
                },

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
                //    typeof(WPR.WindowsCompability.WriteableBitmap)
                //},
                //{
                //    "System.Void System.Windows.Media.Imaging.BitmapSource::SetSource()",
                //    typeof(WPR.WindowsCompability.BitmapSource)
                //},

                {
                    "System.Type System.Type::GetType(System.String,System.Boolean)",
                    typeof(WPR.WindowsCompability.Type2)
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
        /// Per-game IL fixups that can't be expressed as reference redirects (the
        /// <see cref="Patches"/> / <see cref="MemberPatches"/> tables only retarget type/member
        /// references — they don't rewrite a game's own method bodies). Runs after all reference
        /// patching, immediately before the module is written. Every fixup is guarded so it only
        /// touches the exact game it targets and no-ops (rather than corrupting the DLL) if the
        /// expected IL isn't present, e.g. a different build of the title.
        /// </summary>
        /// <summary>
        /// Per-title IL repairs that don't fit the reference-redirect tables. Each entry gates
        /// itself on a type only that game defines, so this is a list of independent fixups —
        /// add to it, and do NOT early-return from here, or every later fixup stops running.
        /// </summary>
        private static void ApplyGameSpecificFixups(ModuleDefinition module)
        {
            ApplyHothRevealFixup(module);
            ApplyFeedMeOilStopByIdFixup(module);
        }

        private static void ApplyHothRevealFixup(ModuleDefinition module)
        {
            // Star Wars: The Battle for Hoth (SWTheBattleForHoth.dll). On a *fresh* game the
            // in-game HUD and the tutorial popups are revealed by animating the sprites in from
            // hidden via CInGameUI/CInGameHelpMode.PlayAnimationForwards, which calls the 4-arg
            // FLAnimation.play overload (animate from tick 0). Under WPR that animated reveal
            // leaves the sprites invisible, so a new game shows an empty HUD (no wave counter,
            // command-point/score panel, buttons or minimap) and blank tutorial boxes. A *resumed*
            // game is unaffected because its restore path (CInGameUI.LoadState) uses the 6-arg
            // play overload, whose startFrame maps past the animation's end so it snaps straight to
            // the final (visible) frame. This rewrites PlayAnimationForwards to use that same
            // snap-open overload so the fresh-game UI is visible too. (The reveal animation is
            // cosmetic; snapping loses the unfold flourish but restores the missing UI. Closing
            // still animates normally — PlayAnimationBackwards is untouched.)
            if (module.GetType("SWTheBattleForHoth.CInGameUI") == null) return;

            foreach (string typeName in new[] { "SWTheBattleForHoth.CInGameUI", "SWTheBattleForHoth.CInGameHelpMode" })
            {
                try
                {
                    TypeDefinition? ty = module.GetType(typeName);
                    MethodDefinition? paf = ty?.Methods.FirstOrDefault(m => m.Name == "PlayAnimationForwards" && m.HasBody);
                    if (paf == null)
                    {
                        Debug.WriteLine($"[hoth-fixup] {typeName}.PlayAnimationForwards not found — skip.");
                        continue;
                    }
                    Debug.WriteLine(SnapOpenReveal(paf)
                        ? $"[hoth-fixup] snapped {typeName}.PlayAnimationForwards to the 6-arg (snap-open) reveal."
                        : $"[hoth-fixup] IL pattern not matched in {typeName}.PlayAnimationForwards — skip.");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[hoth-fixup] {typeName} threw: {ex.Message}");
                }
            }
        }

        /// <summary>
        /// Feed Me Oil (FeedMeOil.dll). Guards the unconditional <c>RemoveAt</c> at the end of
        /// <c>OggSound.StopById</c>, which is what freezes the game on its first music change.
        ///
        /// <para><b>The game's bug.</b> <c>StopById</c> is
        /// <c>found = -1; for (i..) if (_instances[i].id == id) { found = i; …Stop(); break; }
        /// _instances.RemoveAt(found);</c> — the remove runs even when the search fell through and
        /// <c>found</c> is still -1. Reached with a guard, it simply removes nothing, which is
        /// exactly right: nothing matched, so there is nothing to take out of the list.</para>
        ///
        /// <para><b>Why it is reached.</b> <c>SBSounds.playMusic</c> assigns
        /// <c>__lastMusicSound = soundId</c> BEFORE calling <c>stopMusic()</c>, and
        /// <c>stopMusic</c> resolves the sound to stop through that very field. So a change of
        /// track looks for the OUTGOING music's instance id inside the INCOMING sound's instance
        /// list, which can never match.</para>
        ///
        /// <para><b>Why one miss freezes the game for good.</b> The throw happens inside
        /// <c>stopMusic</c> BEFORE it can run <c>__musicId = 0</c>, so the stale id survives; the
        /// next frame takes the same branch and throws again, for ever. It escapes through
        /// <c>SBSceneObjects.onEnter</c> into <c>Director.Update</c>, which is where the game reads
        /// <c>TouchPanel.GetState()</c> and dispatches <c>TouchBegan/Moved/Ended</c> — all of it
        /// below the throw. <c>Draw</c> is a separate call and keeps running, so the game paints a
        /// perfectly good frame while nothing advances and no tap is ever seen. That is the whole
        /// of the reported symptom: "gets into gameplay, then clicks don't register and it freezes."
        /// The guard lets <c>stopMusic</c> finish, <c>__musicId</c> reaches 0, and play resumes.</para>
        ///
        /// <para>Identical on both heads — measured on a Galaxy S24 and on Windows, failing at the
        /// same point (the story cutscene handing over to level 1). Nothing here is Android's.</para>
        /// </summary>
        private static void ApplyFeedMeOilStopByIdFixup(ModuleDefinition module)
        {
            TypeDefinition? ogg = module.GetType("FeedMeOil.OggSound");
            if (ogg == null) return;

            MethodDefinition? m = ogg.Methods.FirstOrDefault(x => x.Name == "StopById" && x.HasBody);
            if (m == null)
            {
                Log.Warn(LogCategory.AppInstall, "[fmo-fixup] FeedMeOil.OggSound.StopById not found — skip.");
                return;
            }

            try
            {
                // Long-form every macro first: inserting instructions lengthens the method, and a
                // pre-existing short branch (there is one — the loop's `break` jumps at the remove)
                // can then no longer reach its target. OptimizeMacros re-shortens what still fits.
                m.Body.SimplifyMacros();

                Instruction? removeAt = m.Body.Instructions.FirstOrDefault(
                    i => (i.OpCode == OpCodes.Callvirt || i.OpCode == OpCodes.Call)
                         && i.Operand is MethodReference mr
                         && mr.Name == "RemoveAt"
                         && mr.DeclaringType.Name.StartsWith("List`1", StringComparison.Ordinal));

                // Argument shape: ldarg.0 ; ldfld _instances ; ldloc <found> ; callvirt RemoveAt.
                Instruction? idxLoad = removeAt?.Previous;
                Instruction? fieldLoad = idxLoad?.Previous;
                Instruction? seqStart = fieldLoad?.Previous;
                Instruction last = m.Body.Instructions[m.Body.Instructions.Count - 1];

                if (removeAt == null || seqStart == null
                    || idxLoad!.OpCode != OpCodes.Ldloc || idxLoad.Operand is not VariableDefinition found
                    || fieldLoad!.OpCode != OpCodes.Ldfld
                    || last.OpCode != OpCodes.Ret)
                {
                    Log.Warn(LogCategory.AppInstall,
                        "[fmo-fixup] StopById IL does not match the expected shape — left unpatched.");
                    m.Body.OptimizeMacros();
                    return;
                }

                ILProcessor il = m.Body.GetILProcessor();
                Instruction guard = Instruction.Create(OpCodes.Ldloc, found);
                Instruction zero = Instruction.Create(OpCodes.Ldc_I4_0);
                Instruction skip = Instruction.Create(OpCodes.Blt, last);

                il.InsertBefore(seqStart, guard);
                il.InsertBefore(seqStart, zero);
                il.InsertBefore(seqStart, skip);

                // Anything that jumped to the old first instruction must now jump to the guard,
                // or the loop's `break` lands past it and the fix silently does nothing.
                foreach (Instruction i in m.Body.Instructions)
                {
                    if (ReferenceEquals(i.Operand, seqStart))
                    {
                        i.Operand = guard;
                    }
                    else if (i.Operand is Instruction[] targets)
                    {
                        for (int t = 0; t < targets.Length; t++)
                        {
                            if (ReferenceEquals(targets[t], seqStart)) targets[t] = guard;
                        }
                    }
                }

                m.Body.OptimizeMacros();
                Log.Info(LogCategory.AppInstall,
                    "[fmo-fixup] guarded FeedMeOil.OggSound.StopById against RemoveAt(-1).");
            }
            catch (Exception ex)
            {
                Log.Warn(LogCategory.AppInstall, $"[fmo-fixup] threw, left unpatched: {ex.Message}");
            }
        }

        /// <summary>
        /// Rewrites a <c>PlayAnimationForwards</c> body from
        /// <c>group.play(startTime, playFlags, speed, notify)</c> (4-arg animate-from-hidden) to
        /// <c>group.play(startTime, 1000, -1, playFlags, speed, notify)</c> (6-arg). The 6-arg
        /// overload multiplies startFrame by ~33.3 to a tick value past the animation's authored
        /// length, so the reveal lands on its final (visible) frame immediately — the same thing
        /// LoadState does on resume. Returns false (leaving the body untouched) if the expected
        /// call sites aren't present.
        /// </summary>
        private static bool SnapOpenReveal(MethodDefinition m)
        {
            Instruction? getScreenTime = null, play4 = null;
            foreach (Instruction i in m.Body.Instructions)
            {
                if ((i.OpCode == OpCodes.Call || i.OpCode == OpCodes.Callvirt) && i.Operand is MethodReference mr)
                {
                    if (mr.Name == "GetScreenAnimationTime") getScreenTime = i;
                    if (mr.Name == "play" && mr.Parameters.Count == 4) play4 = i;
                }
            }
            if (getScreenTime == null || play4 == null) return false;

            // Find the 6-arg play overload on the same declaring type (or a base type).
            MethodReference play4Ref = (MethodReference)play4.Operand;
            MethodDefinition? play6def = null;
            for (TypeDefinition? t = play4Ref.DeclaringType.Resolve(); t != null && play6def == null; t = t.BaseType?.Resolve())
            {
                play6def = t.Methods.FirstOrDefault(x => x.Name == "play" && x.Parameters.Count == 6);
            }
            if (play6def == null) return false;

            ILProcessor il = m.Body.GetILProcessor();
            // Insert startFrame=1000, endFrame=-1 immediately after startTime is pushed, so the
            // stack for the call becomes (group, startTime, 1000, -1, playFlags, speed, notify).
            il.InsertAfter(getScreenTime, il.Create(OpCodes.Ldc_I4, 1000));
            il.InsertAfter(getScreenTime.Next, il.Create(OpCodes.Ldc_I4_M1));
            play4.Operand = m.Module.ImportReference(play6def);
            return true;
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

            AssemblyNameReference? xnaGameServices = null;
            //RnD
            AssemblyNameReference? xnaGameServicesExtensions = null;

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

            // module.AssemblyReferences cycle
            foreach (var refer in module.AssemblyReferences)
            {
                assemblyScopesByOriginalName[refer.Name] = refer;

                if (refer.Name.Contains("Microsoft.Xna"))
                {
                    // Test the more specific "GamerServicesExtensions" first —
                    // "GamerServicesExtensions".Contains("GamerServices") is true,
                    // so the broad check must not run before it (otherwise the
                    // Extensions branch is dead code).
                    if (refer.Name.Contains("GamerServicesExtensions"))
                    {
                        //RnD
                        xnaGameServicesExtensions = refer;
                        // Version 19: the GamerServices types live in WPR.Framework.Xna now, so
                        // this ref must be renamed rather than merely captured. Previously it was
                        // left alone and only individual typerefs were rescoped, which is why
                        // SignedInGamerExtensions needed a hand-written Patches entry.
                        refer.Name = WprFrameworkXnaRef.Name;
                        refer.Version = WprFrameworkXnaRef.Version;
                        refer.PublicKey = WprFrameworkXnaRef.PublicKey;
                    }
                    else if (refer.Name.Contains("GamerServices"))
                    {
                        xnaGameServices = refer;
                        // Version 19: same. Until now this ref kept its WP7 name and bound to our
                        // identity-matching Microsoft.Xna.Framework.GamerServices assembly. That
                        // assembly is gone — its types were absorbed into WPR.Framework.Xna — so
                        // the ref is rewritten instead of preserved.
                        refer.Name = WprFrameworkXnaRef.Name;
                        refer.Version = WprFrameworkXnaRef.Version;
                        refer.PublicKey = WprFrameworkXnaRef.PublicKey;
                    }
                    else
                    {
                        refer.Name = FNARef.Name;
                        refer.Version = FNARef.Version;
                        refer.PublicKey = FNARef.PublicKey;
                    }
                }
                else if (refer.Name.Equals("mscorlib.Extensions",
                    StringComparison.OrdinalIgnoreCase))
                {
                    refer.Name = SystemRunTimeRef.Name;
                    refer.Version = SystemRunTimeRef.Version;
                    refer.PublicKey = SystemRunTimeRef.PublicKey;
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
            foreach (var existingRef in module.GetTypeReferences())
            {
                RescopeTypeReference(existingRef);
            }//for...

            /* GetTypeReferences() above walks the TypeRef metadata TABLE, and a type named inside
             * a custom attribute blob is not in it — the blob carries an assembly-qualified name
             * as a plain string, which no amount of table walking reaches. Those strings need
             * exactly the same rescoping, so hand the same function over them. */
            RescopeCustomAttributeTypeArguments(module, RescopeTypeReference);

            // Points one typeref at whatever WPR assembly owns that type now. A local function so
            // the blob walk above and the table walk share one set of rules; they must agree, or a
            // typeof() in an attribute resolves somewhere its IL counterpart does not.
            void RescopeTypeReference(TypeReference existingRef)
            {
                existingRef.Name = AssemblyNameStandardization.Process(existingRef.Name);

                if (existingRef.FullName
                    == "Microsoft.Xna.Framework.GamerServices.GamerServicesComponent")
                {
                    // GamerServicesComponent is the ONE GamerServices type that did not move to
                    // WPR.Framework.Xna: it derives from FNA's spine GameComponent, so it lives in
                    // WPR.Backend.FNA/Compat/ (same reasoning as GraphicsDeviceManager at v15).
                    // It keeps its Microsoft.Xna.Framework.GamerServices namespace, so only the
                    // scope differs from the rest of the GamerServices surface.
                    existingRef.Scope = FnaBackendRef;
                }
                else if (existingRef.FullName
                    == "Microsoft.Xna.Framework.GamerServicesExtensions.GamerServicesComponent")
                {
                    // Same type, reached through the WP7 GamerServicesExtensions assembly.
                    existingRef.Scope = FnaBackendRef;
                }
                else if (WprFrameworkXnaTypes.Contains(existingRef.FullName))
                {
                    // Value/math types owned by WPR.Framework.Xna (Stage 5a): rescope the game's
                    // ref straight to the owned assembly, overriding the Microsoft.Xna.* -> FNA
                    // rename above (value types share the one Microsoft.Xna.Framework ref with
                    // GraphicsDevice etc., so they can't be split at the assembly-ref level). Games
                    // now bind WPR.Framework.Xna.Vector3 etc. DIRECTLY — no FNA forwarder. Cecil
                    // writes WPR.Framework.Xna into this game's AssemblyReferences at serialize
                    // time; the resolver at the top of PatchDll covers it for the enum-constant
                    // resolve (ContainmentType / PlaneIntersectionType / CurveLoopType / …).
                    existingRef.Scope = WprFrameworkXnaRef;
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

            // Hand a game back the metadata tokens its own assembly shipped with. Same placement
            // reasoning as the call above: the shim assembly is already referenced by now. Must
            // run before the write, which is where Cecil renumbers the TypeDef table.
            PreserveOriginalMetadataTokens(module, Path.GetFileName(modulePath));

            // Same obfuscator, different defect: make its StackTrace caller-identity checks answer
            // "trusted". Independent of the pass above — that one repairs an input WPR itself
            // perturbs, this one a check WPR does not touch at all — so ordering between them does
            // not matter. Placed here because both concern Eazfuscator and read best together.
            NeutraliseObfuscatorStackIdentityChecks(module, Path.GetFileName(modulePath));

            // Game-specific IL fixups that don't fit the reference-redirect tables above.
            ApplyGameSpecificFixups(module);

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
                string[] dlls = Directory.GetFiles(appRootPath, "*.dll", SearchOption.AllDirectories);

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
