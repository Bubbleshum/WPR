using Microsoft.Xna.Framework.Input.Touch;
using Microsoft.Xna.Framework;
using System.Reflection;
using System.Runtime.Loader;
using WPR.Models;
using WPR.Common;

using WPR.SilverlightCompability;
using Microsoft.Phone.Shell;
using Microsoft.Xna.Framework.GamerServices;

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace WPR
{
    public static class ApplicationLaunch
    {
        /// <summary>
        /// <c>[Conditional("DEBUG")]</c> wrapper around <see cref="Trace.WriteLine(string)"/>.
        /// In Release builds the C# compiler elides every call site (including argument
        /// expressions), so an interpolated string like <c>WprTrace($"x={x}")</c> costs
        /// nothing at all — no string allocation, no listener dispatch.
        /// </summary>
        [System.Diagnostics.Conditional("DEBUG")]
        private static void WprTrace(string msg) => Trace.WriteLine(msg);

        // Diagnostic state for the first-chance exception logger (see Start). Hook the
        // AppDomain event once; the [ThreadStatic] flag stops the logger recursing if our
        // own logging throws.
        private static bool _firstChanceHooked;
        [ThreadStatic] private static bool _inFirstChance;

        /// <summary>
        /// The game currently being driven by <see cref="Start"/>, or null if no game is running.
        /// Used by the host so it can request a clean shutdown when the main window closes.
        /// </summary>
        public static Game? CurrentGame { get; private set; }

        /// <summary>
        /// Asks the active game (if any) to exit. Returns true if a game was running.
        /// Safe to call from any thread.
        /// </summary>
        public static bool RequestExit()
        {
            Game? g = CurrentGame;
            if (g == null) return false;
            try { g.Exit(); }
            catch { /* best-effort: process may be tearing down */ }
            return true;
        }

        /// <summary>
        /// The current launch's install folder, captured once at launch. See
        /// <see cref="CurrentProductFolder"/> for why this is captured rather than derived.
        /// </summary>
        private static string? _LaunchProductFolder;

        /// <summary>
        /// Deliberately keep each launched game's object graph reachable for the life of the process,
        /// which prevents the game's own finalizers from ever running.
        ///
        /// <para><b>Why this is ON (2026-08-08).</b> A WP7 game was written for a phone where the
        /// PROCESS DIED at exit, so its finalizers never ran and were never debugged. WPR's launcher
        /// outlives the game, so once the game's objects become collectable those finalizers DO run —
        /// against a world we have already torn down. Storm in a Teacup's
        /// <c>cFrontEnd.Finalize() → unLoadResources()</c> then threw a NullReferenceException, and an
        /// exception escaping a finalizer is FATAL and uncatchable by the host: it killed the whole
        /// launcher. Ironically this only started happening once the ALC-pin fixes succeeded in making
        /// the graph collectable.</para>
        ///
        /// <para><b>The trade.</b> Retaining costs memory (one game's assemblies + heap per launch,
        /// reclaimed when WPR restarts); NOT retaining risks a hard crash caused by third-party code we
        /// cannot fix or guard. Crash beats leak, so we retain. This is a deliberate, temporary policy —
        /// the real fix is hosting each game in its OWN PROCESS, after which the OS reclaims everything
        /// at exit and no game finalizer ever runs in the launcher. Flip this to false once that lands
        /// (or to experiment); everything else in the teardown is unaffected either way.</para>
        /// </summary>
        private const bool RetainGameGraphToAvoidGameFinalizers = true;

        /// <summary>Strong references held only to satisfy <see cref="RetainGameGraphToAvoidGameFinalizers"/>.</summary>
        private static readonly List<object> _RetainedGameGraphs = new List<object>();

        /// <summary>
        /// Install folder for the running game.
        ///
        /// <para><b>Why this is captured and null-safe.</b> It used to be derived live as
        /// <c>Path.Combine(dataPath, Application.Current!.ProductId!)</c> — but
        /// <see cref="ResetWprSingletons"/> calls <c>Application.ResetCurrent()</c> as its FIRST step, so
        /// from that moment on <c>ProductId</c> is null and every evaluation threw
        /// <c>ArgumentNullException (Parameter 'path2')</c>. Two things were silently broken by that:
        /// the teardown markers after the singleton reset (swallowed by TeardownLog's catch, which is
        /// exactly why a hang-on-close looked undiagnosable), and — more seriously — the
        /// <c>Default.Resolving</c> handler, which evaluates this while resolving assemblies and would
        /// throw out of a resolve callback during teardown instead of returning null.</para>
        ///
        /// <para>Deliberately NOT cleared on teardown: it is a plain string (no ALC reference, nothing
        /// to pin) and late finalizers plus the teardown log still need a valid path.</para>
        /// </summary>
        private static string CurrentProductFolder
        {
            get
            {
                string? captured = _LaunchProductFolder;
                if (!string.IsNullOrEmpty(captured))
                {
                    return captured!;
                }
                return ComputeCurrentProductFolder();
            }
        }

        /// <summary>
        /// Derives the install folder from the live <c>Application.Current</c>, IGNORING the captured
        /// value. <see cref="Start"/> must use this (not <see cref="CurrentProductFolder"/>) to seed the
        /// capture, otherwise launch N would read back the folder captured by launch N-1 and every game
        /// after the first in a session would be loaded from the FIRST game's directory.
        /// Null-safe: returns empty once <c>Application.Current</c> has been reset during teardown.
        /// </summary>
        private static string ComputeCurrentProductFolder()
        {
            string? productId = WindowsCompability.Application.Current?.ProductId;
            if (string.IsNullOrEmpty(productId))
            {
                return string.Empty;
            }
            return Path.Combine(
                Configuration.Current.DataPath(Application.DataStoreFolder),
                productId!);
        }

        /// <summary>
        /// The collectible ALC for the currently-running user game, or null between launches.
        /// Set by <see cref="Start"/> right after the ALC is created and cleared after unload.
        /// </summary>
        private static AssemblyLoadContext? _CurrentUserAlc;

        /// <summary>
        /// Simple name (no extension) of the user game's main assembly. Set by <see cref="Start"/>
        /// before the user ALC is published and cleared after unload. Used by the Default-ALC
        /// resolver to distinguish "the main game DLL that FNA needs by name via Type.GetType"
        /// (route into the user ALC) from "any other sibling DLL in the install dir"
        /// (load into Default ALC so non-collectible static refs to its types are legal).
        /// </summary>
        private static string? _CurrentMainAssemblyName;

        /// <summary>
        /// User-install-folder sibling DLLs that the Default-ALC resolver has loaded into the
        /// non-collectible Default ALC. They persist across launches (Default ALC never
        /// unloads), so any per-launch managed statics they carry leak into the next launch
        /// (e.g. PressPlay.FFWD's <c>Application.quitNextUpdate</c> flag would cause the
        /// next launch's <c>Application.Update</c> to call <c>Game.Exit()</c> at frame 0 and
        /// silently close the window). <see cref="ResetWprSingletons"/> walks this list and
        /// clears writable value-type statics + collection contents — see that method for
        /// the exact reset policy. Never cleared (the assemblies stay loaded for the rest
        /// of the WPR process; we just keep adding new ones as we encounter them).
        /// </summary>
        private static readonly List<Assembly> _DefaultAlcUserSiblings = new List<Assembly>();
        private static readonly object _DefaultAlcUserSiblingsLock = new object();

        static ApplicationLaunch()
        {
            // Default-ALC fallback for assemblies that live in the user game's install dir.
            //
            // Two distinct cases, handled differently:
            //
            // 1. The MAIN game DLL (e.g. IDigItWP7.dll, SexyFramework-app.dll). FNA's
            //    ContentTypeReaderManager runs in Default ALC and does Type.GetType
            //    ("Foo.SomeReader, MainDll") to resolve user content readers — it only uses
            //    the returned Type via reflection (no static IL ref), so handing back the
            //    user-ALC-loaded assembly is safe and KEEPS the user types collectible.
            //    Loading the main DLL into Default ALC (as the original implementation did)
            //    permanently contaminates Default with launch-1 user types: their statics
            //    persist across launches and FNA's contentReadersCache pins launch-1 reader
            //    instances forever, manifesting as NREs deep in the next launch (e.g.
            //    Music.RegisterCallBack NRE on a SexyFramework re-launch).
            //
            // 2. SIBLING DLLs (e.g. Chipmunk.dll alongside IDigItWP7.dll). These are real
            //    libraries that non-collectible code can statically reference. If we route
            //    them into the user (collectible) ALC and hand them back to a Default-ALC
            //    requestor, the CLR rejects the resulting non-collectible→collectible
            //    binding with FileLoadException(0x80131515)/NotSupportedException
            //    ("A non-collectible assembly may not reference a collectible assembly").
            //    Symptom: I Dig It threw at frame 1 of IDigItApp.Update on its first
            //    Chipmunk reference. Load siblings into Default ALC instead — they
            //    become non-collectible (they persist across launches), but that's
            //    typically harmless for self-contained native-wrapper libs like Chipmunk.
            //    If a sibling DLL turns out to carry launch-specific managed statics that
            //    leak across launches, deal with it the same way we already deal with
            //    FNA's contentReadersCache: clear it explicitly in ResetWprSingletons.
            AssemblyLoadContext.Default.Resolving += (loadContext, name) =>
            {
                var userAlc = _CurrentUserAlc;
                if (userAlc == null) return null;
                string candidate = Path.Combine(CurrentProductFolder, name.Name + ".dll");
                if (!File.Exists(candidate)) return null;

                bool isMain = _CurrentMainAssemblyName != null &&
                              string.Equals(name.Name, _CurrentMainAssemblyName, StringComparison.OrdinalIgnoreCase);
                try
                {
                    // LoadFromStream rather than LoadFromAssemblyPath: the latter holds
                    // an exclusive file lock on Windows for the life of the loading ALC.
                    // Sibling DLLs go into the non-collectible Default ALC, so a path-
                    // based load would lock the .dll until the WPR process exits —
                    // blocking the in-place Repatch button ("File.Move .dll.original ->
                    // .dll" returns "Access to the path is denied" for every sibling).
                    // Stream-based loads close the file immediately after the bytes are
                    // copied into the ALC's internal metadata heap.
                    Assembly loaded = isMain
                        ? LoadAssemblyWithoutFileLock(userAlc, candidate)
                        : LoadAssemblyWithoutFileLock(loadContext, candidate);
                    if (!isMain)
                    {
                        lock (_DefaultAlcUserSiblingsLock)
                        {
                            if (!_DefaultAlcUserSiblings.Contains(loaded))
                            {
                                _DefaultAlcUserSiblings.Add(loaded);
                            }
                        }
                    }
                    WprTrace($"[wpr-resolve-default] OK   {name.FullName} -> {candidate} target={(isMain ? "user-alc" : "default-alc")} -> {loaded.FullName}");
                    return loaded;
                }
                catch (Exception ex)
                {
                    WprTrace($"[wpr-resolve-default] FAIL {name.FullName} via {candidate} target={(isMain ? "user-alc" : "default-alc")}: {ex.GetType().FullName} hr=0x{ex.HResult:X8} msg=\"{ex.Message}\"");
                    return null;
                }
            };

            // Emulate the WP7 MediaPlayerLauncher round-trip. On real WP7, Show() hands off
            // to the system media player and the app is reactivated when it returns; titles
            // like Star Wars: The Battle for Hoth only continue (CVideoPlayer ->
            // CGame.OnGameActivated -> DoPlayVideoComplete -> LoadLevel) on that reactivation.
            // The launcher facade can't drive the game, so it raises Launched and we post the
            // reactivation onto the game thread. Deferred to the next tick because the title
            // sets its video-complete callback AFTER Show() returns; on the game thread because
            // completion ends up touching the GraphicsDevice. The shim assemblies live in the
            // shared Default ALC, so this single static hook serves every launch.
            Microsoft.Phone.Tasks.MediaPlayerLauncher.Launched += _ =>
            {
                WprTrace("[wpr-trace] MediaPlayerLauncher.Launched -> scheduling app reactivation to complete video");
                WprGameThread.Post(() =>
                {
                    try { PhoneApplicationService.Current?.HandleApplicationStart(false); }
                    catch (Exception ex) { WprTrace("[wpr-ex] MediaPlayerLauncher reactivation threw: " + ex); }
                });
            };
        }

        public static async Task Start(Application app, Action<DisplayOrientation>? requestOrientation = null, Action<Game>? onGameCreated = null)
        {
            if (app.ApplicationType == ApplicationType.Silverlight)
            {
                throw new NotSupportedException(
                    "Silverlight UI runtime is not yet implemented. " +
                    "This XAP installs successfully but cannot be launched yet.");
            }

            if (app.ApplicationType == ApplicationType.ModernNative)
            {
                throw new NotSupportedException(
                    "Modern Native (C++/CX, WinRT) apps are not supported. " +
                    "These ship as native ARM/x86 PE binaries against WinRT — they cannot run on " +
                    "this CLR-based runner without a native loader and a WinRT reimplementation.");
            }

            if (app.ApplicationType != ApplicationType.XNA)
            {
                throw new NotSupportedException(
                    $"Application runtime type '{app.ApplicationType}' is not supported.");
            }

            // Setting game folder path
            WindowsCompability.Application.Current.ProductId = app.ProductId;
            // Same value on the neutral ambient holder. GamerServices reads it from here rather
            // than off the Silverlight Application shim, so the XNA layer needs no dependency on
            // the Silverlight/Avalonia stack. Both are set so either host path behaves identically.
            WPR.Common.WprHostEnvironment.CurrentProductId = app.ProductId;
            // Derive fresh (never through CurrentProductFolder — that would return the PREVIOUS
            // launch's captured folder), then pin it for the rest of this launch INCLUDING teardown,
            // where the singleton it is derived from has already been reset.
            string folderPath = ComputeCurrentProductFolder();
            _LaunchProductFolder = folderPath;

            FNAPlatform.TitleLocation = folderPath;
            // Publish the install folder to the neutral ambient holder so low-level
            // shims (e.g. XElement2.Load) resolve bare relative data reads against it,
            // consistent with the Silverlight path. (The XNA loop also SetCurrentDirectory's
            // to this folder below; this keeps the resolution correct regardless of CWD.)
            WPR.Common.WprHostEnvironment.CurrentInstallFolder = folderPath;
            string curDir = Directory.GetCurrentDirectory();

            // Mirror Debug/Trace output to a per-game log file so we can see exceptions that
            // Game.Tick swallows (Update/Draw/EndDraw catches that only Debug.WriteLine the
            // failure). Without this, a game whose Draw throws every frame appears as a silent
            // black screen with no diagnostic file (since Run() itself doesn't throw).
            //
            // Gated on DEBUG so end users on Release builds don't get a 300 KB-per-launch log
            // file (and don't pay for the per-frame Trace traffic that fills it). To debug a
            // game locally, build the solution in Debug — the per-game wpr_game_debug.log will
            // reappear next to the install directory.
            TextWriterTraceListener? debugListener = null;
#if DEBUG
            try
            {
                string debugLogPath = Path.Combine(folderPath, "wpr_game_debug.log");
                var fs = new FileStream(debugLogPath, FileMode.Create, FileAccess.Write, FileShare.Read);
                var sw = new StreamWriter(fs) { AutoFlush = true };
                sw.WriteLine($"=== WPR game debug log ===");
                sw.WriteLine($"App: {app.Name}  ProductId: {app.ProductId}");
                sw.WriteLine($"Started: {DateTime.UtcNow:o}");
                sw.WriteLine();
                debugListener = new TextWriterTraceListener(sw, "wpr_game_debug");
                Trace.Listeners.Add(debugListener);
                WprTrace("[wpr-trace] ApplicationLaunch: trace listener attached (smoke test)");
                InstallCrashHooks();
                Backend.FNA.TeardownDiagnostics.RecordLaunchBaseline();
                TeardownLog($"===== launch: {app.Name} ({app.ProductId}) =====");

                // Surface exceptions that game code swallows in broad catch(Exception){}
                // blocks. FirstChanceException fires at throw time, before any catch, so the
                // real type/message/stack is captured even when the game discards it. This is
                // exactly the case for ports like RISK whose XNAGame.Update wraps the whole
                // scene-load chain in catch{} — a package-load throw there leaves the game
                // stuck on its 2-sprite logo (black screen) with nothing logged. Debug-only
                // (this whole block is #if DEBUG) and hooked once per process.
                if (!_firstChanceHooked)
                {
                    _firstChanceHooked = true;
                    AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
                    {
                        if (_inFirstChance) return;
                        _inFirstChance = true;
                        try
                        {
                            Exception fce = e.Exception;
                            WprTrace($"[wpr-fce] {fce.GetType().FullName}: {fce.Message}");
                            WprTrace("[wpr-fce] " + (fce.StackTrace ?? "(no stack)"));
                        }
                        catch { /* never let logging mask the original throw */ }
                        finally { _inFirstChance = false; }
                    };
                }
            }
            catch (Exception ex)
            {
                Log.Warn(LogCategory.AppList, $"Could not attach debug log listener: {ex.Message}");
            }
#endif

            // Use a collectible ALC so closing the game can unload the user assembly — otherwise
            // the .dll stays locked on disk for the life of WPR (blocking re-install) and any
            // statics in the user assembly (timers, threads, caches) leak forever.
            string asmPath = Path.Combine(folderPath, AssemblyNameStandardization.Process(app.Assembly));
            var alc = new AssemblyLoadContext(
                $"WPR.XnaApp[{Path.GetFileNameWithoutExtension(asmPath)}]",
                isCollectible: true);
            // Held in a local so teardown can DETACH it. Leaving it attached is what stopped the ALC
            // ever unloading: this lambda's closure also captures `assem`, `mainType` and `obj` (they
            // share one compiler-generated display class), so the ALC's own Resolving event kept a
            // RuntimeType from the game assembly alive → which kept its LoaderAllocator alive → which
            // is precisely what "collectible ALC" unload waits on. Proven by gcroot on a heap dump:
            //   strong handle → AssemblyLoadContext → Func<ALC,AssemblyName,Assembly>
            //     → ApplicationLaunch+<>c__DisplayClass17_0 → RuntimeType → LoaderAllocator
            //     → …game objects… → <Game subclass>
            Func<AssemblyLoadContext, AssemblyName, Assembly?> userAlcResolver = (ctx, name) =>
            {
                // Satellite (localized) resource assemblies live in a per-culture SUBFOLDER
                // (e.g. <folder>\en\battleship.resources.dll), not the install root. The CLR
                // requests them as "<name>.resources, Culture=<c>"; the flat probe below only
                // looks in the root and misses, so the game's ResourceManager string table never
                // loads and ALL on-screen text renders blank (Battleship's getString() reads
                // BS_Resource from battleship.resources.dll). Probe the culture subfolder first.
                // A missing file falls through to the normal probes, and the CLR walks parent
                // cultures (en-GB -> en -> neutral) via separate Resolving requests.
                if (!string.IsNullOrEmpty(name.CultureName))
                {
                    string satellitePath = Path.Combine(folderPath, name.CultureName, name.Name + ".dll");
                    if (File.Exists(satellitePath))
                    {
                        try
                        {
                            var sat = LoadAssemblyWithoutFileLock(ctx, satellitePath);
                            WprTrace($"[wpr-resolve-user] OK   {name.FullName} -> {satellitePath} (satellite)");
                            return sat;
                        }
                        catch (Exception ex)
                        {
                            WprTrace($"[wpr-resolve-user] FAIL satellite {name.FullName} via {satellitePath}: {ex.GetType().FullName} hr=0x{ex.HResult:X8} msg=\"{ex.Message}\"");
                            return null;
                        }
                    }
                }

                string candidate = Path.Combine(folderPath, name.Name + ".dll");
                if (File.Exists(candidate))
                {
                    try
                    {
                        // LoadFromStream rather than LoadFromAssemblyPath — see
                        // matching comment in the Default.Resolving handler above.
                        // Even though this ALC is collectible (lock released on
                        // Unload), Unload completion is asynchronous; using a stream
                        // load releases the file lock immediately after load so the
                        // Repatch button works even before the ALC has actually
                        // unloaded.
                        var loaded = LoadAssemblyWithoutFileLock(ctx, candidate);
                        WprTrace($"[wpr-resolve-user] OK   {name.FullName} -> {candidate} -> {loaded.FullName}");
                        return loaded;
                    }
                    catch (Exception ex)
                    {
                        // The runtime will re-raise FileLoadException for this name after we
                        // return null / let it propagate; log the actual underlying failure
                        // here so we can see HResult and exception type that the surface error
                        // hides ("Operation is not supported (0x80131515)" being the typical
                        // opaque case).
                        WprTrace($"[wpr-resolve-user] FAIL {name.FullName} via {candidate}: {ex.GetType().FullName} hr=0x{ex.HResult:X8} msg=\"{ex.Message}\"");
                        if (ex.InnerException != null)
                            WprTrace($"[wpr-resolve-user] FAIL inner: {ex.InnerException.GetType().FullName}: {ex.InnerException.Message}");
                        WprTrace($"[wpr-resolve-user] FAIL stack: {ex.StackTrace}");
                        throw;
                    }
                }

                // Fall back to name-only load — handles WinMD-projected refs like
                // `mscorlib v=255.255.255.255` that the strict version binder rejects.
                try
                {
                    var loaded = AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName(name.Name!));
                    WprTrace($"[wpr-resolve-user] DEF  {name.FullName} -> {loaded.FullName}");
                    return loaded;
                }
                catch (Exception ex)
                {
                    WprTrace($"[wpr-resolve-user] MISS {name.FullName}: {ex.GetType().FullName} hr=0x{ex.HResult:X8} msg=\"{ex.Message}\"");
                    return null;
                }
            };
            alc.Resolving += userAlcResolver;
            // Publish the ALC so the Default.Resolving handler routes user-assembly loads
            // here instead of contaminating the default ALC. Must be set BEFORE LoadFromAssemblyPath
            // because the main assembly's load can trigger dependency resolution that hits the
            // default ALC handler. The main-assembly name is published alongside so the
            // Default-ALC handler can tell the main game DLL apart from sibling library DLLs
            // (they're routed differently — see the static ctor).
            _CurrentMainAssemblyName = Path.GetFileNameWithoutExtension(asmPath);
            _CurrentUserAlc = alc;
            // Stream load (no file lock) — matches the policy used by both Resolving
            // handlers above. Required so the Repatch button can rewrite the main
            // DLL on disk after a launch even if the user ALC hasn't fully unloaded.
            Assembly assem = LoadAssemblyWithoutFileLock(alc, asmPath);

            Directory.SetCurrentDirectory(folderPath);

            // Instatiate
            Type? mainType = assem.GetType(app.EntryPoint);

            // Run on separate thread to not affect the UI
            await Task.Run(() =>
            {
                // Set this BEFORE the Game subclass is instantiated. Many WP7 games probe
                // TouchPanel.GetCapabilities().IsConnected from their constructor or static
                // init paths and cache the result for the rest of the session. If MouseAsTouch
                // is still false at ctor time, GetCapabilities returns IsConnected=false,
                // the cached "no touch" decision sticks, and the game stops handling taps —
                // which exactly matches the "Angry Birds buttons never respond" and
                // "Zombies touch dies after scene change" reports. The setter on FNA's
                // TouchPanel (with our WPR-side patch) eagerly flips TouchDeviceExists=true
                // when MouseAsTouch goes true, so GetCapabilities reports a real device on
                // the very first read.
#if !__MOBILE__
                TouchPanel.MouseAsTouch = true;
#endif
                TouchPanel.EnabledGestures = GestureType.DoubleTap | GestureType.Tap | GestureType.Hold |
                    GestureType.HorizontalDrag | GestureType.VerticalDrag | GestureType.FreeDrag |
                    GestureType.Pinch | GestureType.Flick | GestureType.DragComplete | GestureType.PinchComplete;

                // Manage Game lifetime explicitly (no using) so we can interleave teardown:
                // Game.Dispose has to run BEFORE FAudio is destroyed (Game.Content.Dispose
                // disposes SoundEffectInstances which destroy their FAudio source voices —
                // those calls are invalid once the FAudio engine handle is released), but
                // ALC unload and WPR-side singleton reset have to run AFTER Game.Dispose
                // (the game's Dispose may still touch our shims).
                // Reset gamer-services state BEFORE the Game subclass is instantiated.
                // Game ctors typically register a SignedIn handler from inside the ctor;
                // if a prior launch left FirstSignInSessionDone == true, the handler would
                // fire synchronously during `+=` (before the ctor finishes initialising
                // its fields), which NREs e.g. Assassin's Creed XNAGame.
                SignedInGamer.Reset();

                // CRITICAL: the entire launch (ctor + lifecycle + Game.Dispose) is wrapped
                // in an outer try/finally so the teardown block at the bottom (which runs
                // TeardownAudioState, ResetWprSingletons — including ResetSiblingRegistry
                // that clears static dicts in Default-ALC sibling DLLs — debug-listener
                // disposal, and the user ALC unload) ALWAYS executes, even when
                // Activator.CreateInstance throws.
                //
                // Why it matters: Kinectimals' MainGame.ctor crashes on
                // `Resource.ManagedResource<T>.s_resources.Add("BlackTex", ...)` when a
                // prior launch in the same WPR session left that static dict populated.
                // Without this outer try/finally, an Activator throw propagated out of
                // the lambda entirely — no debug-listener disposal (so the wpr_game_debug.log
                // file stayed open and the NEXT launch logged "being used by another
                // process"), no ResetSiblingRegistry (so the dict stayed populated and
                // EVERY subsequent launch crashed the same way), no ALC unload. One bad
                // launch cascaded into "this game can never be launched again until WPR
                // is restarted."
                Game? obj = null;
                try
                {
                    obj = Activator.CreateInstance(mainType!) as Game;
                try
                {
                    CurrentGame = obj;
                    obj!.IsMouseVisible = true;
                    // Prefer the curated name from the game's achievement catalogue
                    // (Database/Achievements/<productId>/achievements.json "Name") when one ships;
                    // otherwise fall back to the app's friendly manifest title.
                    string displayName = HardcodedAchievementCatalogue.GameName(app.ProductId) ?? app.Name;
                    obj!.Window.Title = $"{displayName} - {app.Author} (Publisher: {app.Publisher})";

                    // Hook for the host to decorate the just-created SDL window (e.g. set the
                    // game's icon via SDL_SetWindowIcon). Best-effort: a host that throws here
                    // shouldn't prevent the game from running.
                    if (onGameCreated != null)
                    {
                        try { onGameCreated(obj); }
                        catch (Exception ex) { Log.Warn(LogCategory.AppList, $"onGameCreated hook threw: {ex.Message}"); }
                    }

                    // Re-affirm in case the user game's ctor reset these (it shouldn't, but
                    // the cost is one static field write).
#if !__MOBILE__
                    TouchPanel.MouseAsTouch = true;
#endif

                    WPR.Backend.FNA.Compat.GraphicsDeviceManager.RequestOrientation = requestOrientation;
                    GamerServicesDispatcher.WindowHandle = obj.Window.Handle;

                    WprTrace("[wpr-trace] ApplicationLaunch: subscribing to obj.Activated");
                    obj.Activated += (obj, args) =>
                    {
                        WprTrace("[wpr-trace] ApplicationLaunch: obj.Activated fired -> HandleApplicationStart(true)");
                        PhoneApplicationService.Current!.HandleApplicationStart(true);
                    };

                    // FNA only raises Game.Activated when the SDL window receives a focus event.
                    // On some setups (the Avalonia host already had focus, or SDL didn't deliver
                    // the focus-gained event for a freshly-created game window) the event never
                    // fires and the WP7 lifecycle callbacks (Launching / Activated) never run —
                    // the game then sits on a "not initialised" black screen because its
                    // Application_Launching handler is where it builds its scene graph.
                    // Fire the lifecycle once, unconditionally, right at startup as a safety net.
                    WprTrace("[wpr-trace] ApplicationLaunch: priming PhoneApplicationService.HandleApplicationStart(true) before Game.Run");
                    try
                    {
                        PhoneApplicationService.Current!.HandleApplicationStart(true);
                    }
                    catch (Exception ex)
                    {
                        WprTrace("[wpr-ex] HandleApplicationStart priming threw: " + ex);
                    }

                    GraphicsDeviceManager? manager = obj.Services.GetService(typeof(IGraphicsDeviceManager)) as GraphicsDeviceManager;
                    if (manager != null)
                    {
                        manager.PreparingDeviceSettings += (obj, args) =>
                        {
                            WPR.Backend.FNA.Compat.GraphicsDeviceManager.RequestOrientationChange(
                                args.GraphicsDeviceInformation.PresentationParameters.BackBufferWidth,
                                args.GraphicsDeviceInformation.PresentationParameters.BackBufferHeight
                            );
                        };
                    }

                    try
                    {
                        // WPR: suppress focus-driven IsActive flips across the launch/boot window.
                        // A transient FOCUS_LOST during startup (something briefly steals the SDL
                        // window's focus around sign-in) flips Game.IsActive→false and fires
                        // OnDeactivated mid-boot. Engines that pause their game clock on deactivate
                        // (the jamdat/EA-Mobile RISK port calls GetGlobalTimeSystem().Pause() from
                        // GameApp.FocusLost) then freeze on their logo screen forever, because SDL
                        // delivers no paired FOCUS_GAINED to resume them — the game sits on a black
                        // screen. Arm the same guard the achievement toast uses so the boot blip is
                        // ignored; a genuine alt-tab a few seconds later is still honored. See
                        // WprActivationGuard and SDL2_FNAPlatform's FOCUS_LOST/GAINED branches.
                        WprActivationGuard.SuppressFocusActivation(TimeSpan.FromSeconds(8));

                        WprTrace("[wpr-trace] ApplicationLaunch: about to call Game.Run()");
                        // Run the game and capture any exceptions to produce richer diagnostics.
                        try
                        {
                            obj.Run();
                            WprTrace("[wpr-trace] ApplicationLaunch: Game.Run() returned normally");
                        }
                        catch (Exception ex)
                        {
                            try
                            {
                                string diag = BuildDiagnostics(obj, ex);

                                // Log to the configured logger
                                Log.Error(LogCategory.AppList, $"Game threw during Run: {ex}");

                                // Also try to write diagnostics to a file next to the game's folder so it survives process exits
                                try
                                {
                                    string diagFile = Path.Combine(folderPath, "wpr_game_diagnostic.txt");
                                    File.WriteAllText(diagFile, diag);
                                }
                                catch (Exception fileEx)
                                {
                                    Log.Warn(LogCategory.AppList, $"Failed to write diagnostic file: {fileEx}");
                                }
                            }
                            catch (Exception logEx)
                            {
                                // Avoid swallowing the original exception but at least log that diagnostics failed
                                Log.Warn(LogCategory.AppList, $"Diagnostics failed: {logEx}");
                            }

                            // Rethrow so outer code still receives the original failure
                            throw;
                        }

                        try
                        {
                            PhoneApplicationService.Current!.HandleApplicationExit();
                        }
                        catch (Exception ex)
                        {
                            Log.Warn(LogCategory.AppList, $"Ignored clean-up exception:\n {ex}");
                        }

                        obj.Exit();
                    }
                    finally
                    {
                        CurrentGame = null;
                        // Ensure current directory is restored to previous value to avoid surprising callers
                        try
                        {
                            Directory.SetCurrentDirectory(curDir);
                        }
                        catch { }

                        // MediaPlayer.Stop has to happen while FAudio is still alive — it sends
                        // an FAudio call. The rest of audio teardown waits until after Game.Dispose.
                        try { Microsoft.Xna.Framework.Media.MediaPlayer.Stop(); }
                        catch (Exception ex) { Log.Warn(LogCategory.AppList, $"MediaPlayer.Stop on teardown threw: {ex.Message}"); }
                    }
                }
                finally
                {
                    // Game.Dispose runs here. It walks Components, Content (disposes loaded
                    // SoundEffects → SoundEffectInstances → destroys their FAudio source voices),
                    // GraphicsDevice, and the SDL window. All of that requires FAudio to still
                    // be alive, which is why TeardownAudioState is below.
                    try { obj?.Dispose(); }
                    catch (Exception ex) { Log.Warn(LogCategory.AppList, $"Game.Dispose threw: {ex.Message}"); }

                    // GUARANTEE the SDL window is gone before this thread stops pumping.
                    //
                    // FNA's Game.Dispose only destroys the window inside `if (!isDisposed)`, so a game
                    // whose Game subclass overrides Dispose without calling base — or that disposed
                    // itself earlier during exit — leaks it. That leak is fatal to the LAUNCHER, not
                    // just the game: FNA creates the window on this thread, which is a thread-POOL
                    // thread, and a pool thread returns to the pool instead of exiting — so Windows
                    // never auto-destroys the thread's windows the way it would for a real thread exit.
                    // The result is a visible top-level window of WPR.Platform.Windows.exe with nothing
                    // pumping its messages, and Windows terminates the whole process as hung
                    // ("Application Hang", Hang Type 0x8000000). Confirmed for Storm in a Teacup: a live
                    // class="SDL_app" window remained after a clean teardown, which also pinned the ALC.
                    //
                    // A surviving window IS the proof that Game.Dispose did not destroy it, so this is
                    // not a double-free. It must run HERE, on the window's creating thread — Win32
                    // DestroyWindow only works from that thread, and by the outer finally we may not
                    // still be on it.
                    try
                    {
                        if (obj?.Window != null && Backend.FNA.TeardownDiagnostics.HasLiveSdlWindow())
                        {
                            TeardownLog("window leak: Game.Dispose left the SDL window alive — forcing DisposeWindow");
                            FNAPlatform.DisposeWindow(obj.Window);
                            TeardownLog("window leak: forced DisposeWindow done, stillAlive=" +
                                        Backend.FNA.TeardownDiagnostics.HasLiveSdlWindow());
                        }
                    }
                    catch (Exception ex)
                    {
                        Log.Warn(LogCategory.AppList, $"Forced SDL window dispose threw: {ex.Message}");
                        TeardownLog("window leak: forced DisposeWindow THREW: " + ex);
                    }

                    // Drop the Game reference here, while it is still in scope. It is captured by this
                    // method's display class, which the ALC's Resolving handler keeps alive — so
                    // leaving it set pins the game's entire object graph and blocks the ALC unload.
                    if (RetainGameGraphToAvoidGameFinalizers && obj != null)
                    {
                        // ...except we deliberately keep it reachable, so the game's finalizers never
                        // run against our torn-down world and crash the launcher. See the field's docs.
                        lock (_RetainedGameGraphs)
                        {
                            _RetainedGameGraphs.Add(obj);
                        }
                        TeardownLog("retain: game graph deliberately kept alive (game-finalizer safety); " +
                                    $"retainedLaunches={_RetainedGameGraphs.Count}");
                    }
                    obj = null;
                }
                }  // end inner try (Activator.CreateInstance + game lifecycle)
                finally
                {
                // Now safe to tear down the audio engine itself: every SoundEffectInstance has
                // released its source voice via Content.Dispose above.
                TeardownLog("teardown: audio state");
                TeardownAudioState();
                TeardownLog("teardown: audio state done");

                // Drop user-assembly references then unload the ALC so the .dll is freed and any
                // statics inside the user assembly (timers, caches) can be reclaimed.
                //
                // Order matters here — the previous "Unload() + one GC.Collect/Wait/Collect"
                // sequence (the MSDN naive example) wasn't sufficient for Kinectimals: its
                // Resource.ManagedResource<T> holds a static Dictionary keyed by resource
                // name with no Clear method, so the second launch in the same WPR process
                // threw `ArgumentException: An item with the same key has already been added.
                // Key: BlackTex` from inside ManagedResource<T>..ctor on Dictionary.Add. ALC
                // unload is asynchronous and only completes when zero references reach into
                // the ALC from outside — common pin sources here:
                //   1. _CurrentUserAlc / _CurrentMainAssemblyName statics read by the
                //      Default.Resolving handler;
                //   2. the debug TraceListener — if a user-assembly type wrote to Trace,
                //      the listener's pipeline may keep the call-site type alive;
                //   3. JIT may pin `alc` on the calling stack frame if the unload runs
                //      inline (only released when the method returns).
                // Mitigations applied below in order: clear the statics, remove and dispose
                // the trace listener, then call Unload() from a non-inlined helper while
                // iterating GC cycles against a WeakReference until the ALC is genuinely
                // collected (or we hit the cap and log the failure).
                try
                {
                    PhoneApplicationService.Current?.HandleApplicationExit();
                    ResetWprSingletons();

                    TeardownLog("singletons reset; detaching trace listener");

                    // 2 of the 3 above — remove the debug listener BEFORE Unload(). Any
                    // Trace.WriteLine from user-assembly type init that ran during this
                    // launch flowed through this listener; nulling it now releases those
                    // captured frames before the ALC tries to unload.
                    if (debugListener != null)
                    {
                        try
                        {
                            Trace.Listeners.Remove(debugListener);
                            debugListener.Flush();
                            debugListener.Close();
                            debugListener.Dispose();
                        }
                        catch { /* best-effort: we're on the teardown path */ }
                        debugListener = null;
                    }

                    // 1 + 3 — null the statics and call Unload() from a non-inlined helper
                    // so the JIT can't pin `alc` on this stack frame. The helper returns a
                    // WeakReference that we poll across GC cycles.
                    // Release everything of the GAME'S that this method's closure still holds, or the
                    // ALC can never unload. `assem`/`mainType` (and the Game instance, nulled where it
                    // goes out of scope above) live in the same compiler-generated display class as the
                    // Resolving lambda, so while that handler is attached the ALC transitively roots a
                    // RuntimeType from the game assembly → its LoaderAllocator → itself. Detaching the
                    // handler AND clearing the captured fields breaks both directions.
                    // (Root chain proven by gcroot on a heap dump — see the comment at the registration.)
                    // Must run while we still have the ALC: handlers are identified by which load
                    // context their declaring assembly came from.
                    StripUserAlcHandlersFromAppDomainEvents(alc);

                    try { alc.Resolving -= userAlcResolver; }
                    catch (Exception ex) { TeardownLog("alc-unload: detach resolver threw: " + ex.Message); }
                    userAlcResolver = null!;
                    mainType = null;
                    assem = null!;

                    TeardownLog("alc-unload: begin");
                    WeakReference alcRef = BeginUserAlcUnload(alc);
                    alc = null!;  // drop our local strong ref

                    int rounds = 0;
                    const int MaxRounds = 12;
                    while (alcRef.IsAlive && rounds < MaxRounds)
                    {
                        GC.Collect();
                        GC.WaitForPendingFinalizers();
                        GC.Collect();
                        rounds++;
                        TeardownLog($"alc-unload: round {rounds} done, alive={alcRef.IsAlive}");
                    }

                    if (alcRef.IsAlive)
                    {
                        Log.Warn(LogCategory.AppList,
                            $"ALC failed to unload after {rounds} GC cycles. Static state " +
                            $"from this launch will leak into the next launch of any game in " +
                            $"this WPR session — restart WPR if the next launch hits a " +
                            $"\"duplicate key\" / \"already added\" exception.");
                        WprTrace($"[wpr-alc] FAILED to unload after {rounds} GC cycles — ALC still alive");
                        TeardownLog($"alc-unload: FAILED after {rounds} rounds — ALC still alive");
                    }
                    else
                    {
                        WprTrace($"[wpr-alc] unloaded after {rounds} GC cycle(s)");
                        TeardownLog($"alc-unload: OK after {rounds} round(s)");
                    }

                    // Close-failure investigation: WER reports an unresponsive TOP-LEVEL WINDOW long
                    // after this point, and FNA's SDL window lives in this process — so record what
                    // windows/threads survive the teardown.
                    TeardownLog("post-teardown: " + Backend.FNA.TeardownDiagnostics.Describe());
                    // Candidate ALC roots held by the WPR-owned XNA layer (the ALC still fails to
                    // unload even now the leaked window is gone — different cause, still open).
                    TeardownLog("post-teardown retained: " + WPR.Xna.Rhi.XnaRetainedState.Describe());
                }
                catch (Exception ex)
                {
                    Log.Warn(LogCategory.AppList, $"ALC unload best-effort failed: {ex}");
                }
                }  // end outer finally (ensures teardown runs even when ctor throws)
            });
        }

        /// <summary>
        /// Non-inlined helper that null-clears the statics referencing the user ALC and
        /// calls <c>alc.Unload()</c>. Returning a <see cref="WeakReference"/> to the ALC
        /// lets the caller iterate GC cycles until the ALC is genuinely collected.
        ///
        /// <para>This MUST be a separate non-inlined method. If <c>alc.Unload()</c> were
        /// called from <see cref="Start"/> directly, the JIT could keep <c>alc</c> rooted
        /// on the caller's stack frame for the rest of the method body, and the subsequent
        /// <c>GC.Collect()</c> would not free the ALC even though no real reference exists.
        /// Crossing a method boundary forces the local out of the caller's stack-tracked
        /// roots once this method returns.</para>
        /// </summary>
        [MethodImpl(MethodImplOptions.NoInlining)]
        private static WeakReference BeginUserAlcUnload(AssemblyLoadContext alc)
        {
            // Default.Resolving (registered in the static ctor) reads _CurrentUserAlc on
            // every bind — until we null it, every assembly lookup pins the ALC.
            _CurrentUserAlc = null;
            _CurrentMainAssemblyName = null;

            var weak = new WeakReference(alc);
            alc.Unload();
            return weak;
        }

        /// <summary>
        /// Loads an assembly from disk WITHOUT holding a file lock for the life of
        /// the loading ALC. <c>LoadFromAssemblyPath</c> on Windows opens the file
        /// with FILE_SHARE_READ but no FILE_SHARE_WRITE / FILE_SHARE_DELETE, and the
        /// handle stays open until the ALC is collected — for the Default ALC that
        /// means forever, so a subsequent <c>File.Move</c> over the .dll (e.g. from
        /// <see cref="ApplicationInstaller.RepatchAsync"/>'s "restore from .original"
        /// step) returns "Access to the path is denied" for every game DLL.
        /// <see cref="AssemblyLoadContext.LoadFromStream(Stream)"/> copies the bytes
        /// into the ALC's internal metadata heap and closes the underlying file
        /// stream when this method returns, so the path is immediately writable.
        ///
        /// <para>Side effect: <c>Assembly.Location</c> on the returned assembly is
        /// empty string rather than the on-disk path. The shims and FNA code we
        /// route through don't depend on Location — content loads use
        /// <c>TitleContainer.OpenStream</c> rooted at <c>FNAPlatform.TitleLocation</c>,
        /// type resolution uses Type.GetType against module-relative names, and the
        /// patcher runs at install time on actual files. If a future game turns out
        /// to call <c>Assembly.GetExecutingAssembly().Location</c>, we'd add a
        /// patcher entry to fix that callsite rather than reverting to path-based
        /// loads here.</para>
        /// </summary>
        private static Assembly LoadAssemblyWithoutFileLock(AssemblyLoadContext ctx, string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            using var ms = new MemoryStream(bytes, writable: false);
            return ctx.LoadFromStream(ms);
        }

        /// <summary>
        /// Tear down FNA's audio state so a song started via <c>MediaPlayer.Play</c> doesn't
        /// keep playing after <see cref="Game.Dispose"/> and so the audio subsystems aren't
        /// left bound to a destroyed FAudio engine when the next game launches. Three layers,
        /// in this order:
        /// <list type="number">
        ///   <item><description><see cref="Microsoft.Xna.Framework.Media.MediaPlayer.DisposeIfNecessary"/>
        ///     calls <c>FAudio.XNA_SongQuit</c> and clears MediaPlayer's <c>initialized</c>
        ///     static — without this, MediaPlayer skips re-init on the next launch because
        ///     <c>initialized</c> is still true, but the song subsystem it's pointing at was
        ///     bound to a now-destroyed FAudio engine, and subsequent <c>XNA_PlaySong</c>
        ///     calls hit dangling internal state.</description></item>
        ///   <item><description>Reflective dispose of FNA's internal <c>FAudioContext.Context</c>
        ///     singleton releases the master voice and the FAudio engine handle. FNA only ever
        ///     nulls this from inside its own Dispose() so we have to drive that path. Caller
        ///     must have already disposed every live <c>SoundEffectInstance</c>: those hold
        ///     FAudio source voices that need to be destroyed via the still-live engine.</description></item>
        /// </list>
        /// Caller is responsible for stopping <c>MediaPlayer</c> while FAudio is still alive
        /// (it issues an FAudio call). All steps here are best-effort: any failure is logged
        /// and swallowed because we're already on the teardown path.
        /// </summary>
        /// <summary>
        /// Appends a teardown marker to <c>wpr_teardown.log</c> next to the game's install folder,
        /// writing DIRECTLY to the file rather than through <c>Trace</c>.
        ///
        /// <para><b>Why this exists.</b> The ordered teardown removes the debug TraceListener BEFORE the
        /// ALC unload (it can pin user-assembly frames), so everything after that point — the unload
        /// itself, the GC/finalizer rounds, and any failure in them — was invisible in
        /// <c>wpr_game_debug.log</c>. That blind spot cost two rounds of guesswork on a
        /// hang-on-close, so the teardown tail now logs independently of Trace. Always on (not
        /// DEBUG-gated): it is a handful of lines per launch and it is the only record of the phase
        /// where the ALC-unload / stuck-audio / duplicate-static failures actually happen.</para>
        /// </summary>
        private static void TeardownLog(string message)
        {
            try
            {
                string dir = CurrentProductFolder;
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
                {
                    return;
                }
                File.AppendAllText(
                    Path.Combine(dir, "wpr_teardown.log"),
                    $"{DateTime.UtcNow:HH:mm:ss.fff} [tid {Environment.CurrentManagedThreadId}] {message}{Environment.NewLine}");
            }
            catch { /* diagnostics must never affect teardown */ }
        }

        /// <summary>
        /// Installs process-wide handlers that record failures which would otherwise leave NO trace:
        /// an exception escaping a finalizer (the finalizer thread runs during the ALC-unload GC
        /// rounds, and an escape there kills the process silently) or an unobserved task fault.
        /// Idempotent — only the first call subscribes.
        /// </summary>
        private static int _crashHooksInstalled;
        private static void InstallCrashHooks()
        {
            if (Interlocked.Exchange(ref _crashHooksInstalled, 1) != 0)
            {
                return;
            }

            AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            {
                TeardownLog("!!! UNHANDLED EXCEPTION (process is going down): " +
                            (e.ExceptionObject?.ToString() ?? "(null)"));
            };

            TaskScheduler.UnobservedTaskException += (_, e) =>
            {
                TeardownLog("!!! UNOBSERVED TASK EXCEPTION: " + e.Exception);
            };
        }

        /// <summary>
        /// Removes handlers belonging to the exiting game from the process-wide BCL events it may have
        /// subscribed to and never unsubscribed from.
        ///
        /// <para><b>Why.</b> WP7 games commonly do
        /// <c>AppDomain.CurrentDomain.UnhandledException += MyHandler</c> in their Game ctor. That event
        /// lives for the life of the PROCESS, so the delegate keeps the game's <c>Game</c> instance —
        /// and therefore its whole object graph and its collectible AssemblyLoadContext — alive forever.
        /// Confirmed by gcroot on a heap dump:
        ///   strong handle → UnhandledExceptionEventHandler → &lt;game&gt;.cMainGame → … </para>
        ///
        /// <para>We cannot <c>-=</c> a delegate we never held, so the invocation list is filtered by
        /// reflection: any handler whose declaring assembly was loaded into the game's ALC is dropped,
        /// and everything else (notably WPR's own crash hook) is preserved. Best-effort — a failure
        /// here only costs the unload, never correctness.</para>
        /// </summary>
        private static void StripUserAlcHandlersFromAppDomainEvents(AssemblyLoadContext? userAlc)
        {
            if (userAlc == null)
            {
                return;
            }

            try
            {
                // Locate the backing field by TYPE, not by name: on .NET Core/8
                // AppDomain.UnhandledException just forwards to a STATIC event on AppContext, so the
                // old guess of an AppDomain instance field named "_unhandledException" never matched
                // (the teardown log said so). Searching by field type survives both layouts and any
                // future rename.
                object? owner = null;
                FieldInfo? field = FindDelegateField(
                    typeof(AppContext), BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
                if (field == null)
                {
                    field = FindDelegateField(
                        typeof(AppDomain), BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
                    owner = AppDomain.CurrentDomain;
                }

                if (field == null)
                {
                    TeardownLog("appdomain-events: no UnhandledExceptionEventHandler field found on AppContext/AppDomain");
                    return;
                }

                var current = field.GetValue(owner) as UnhandledExceptionEventHandler;
                if (current == null)
                {
                    return;
                }

                UnhandledExceptionEventHandler? kept = null;
                int removed = 0;
                foreach (Delegate d in current.GetInvocationList())
                {
                    // Check the delegate's TARGET as well as its declaring type. The handler that
                    // actually pinned the ALC was FNA's `Game.OnUnhandledException` — declared in FNA,
                    // but BOUND TO the game's Game subclass instance. Filtering on the declaring
                    // assembly alone matched nothing (log said removedGameOwned=0); what makes it
                    // game-owned is the receiver, whose runtime type lives in the game's ALC.
                    bool fromUserAlc =
                        IsFromUserAlc(d.Method.DeclaringType?.Assembly, userAlc) ||
                        IsFromUserAlc(d.Target?.GetType().Assembly, userAlc);
                    if (fromUserAlc)
                    {
                        removed++;
                        continue;
                    }
                    kept = (UnhandledExceptionEventHandler) Delegate.Combine(
                        kept, d)!;
                }

                if (removed > 0)
                {
                    field.SetValue(owner, kept);
                }
                TeardownLog($"appdomain-events: UnhandledException handlers total={current.GetInvocationList().Length} " +
                            $"removedGameOwned={removed} (via {field.DeclaringType?.Name}.{field.Name})");
            }
            catch (Exception ex)
            {
                TeardownLog("appdomain-events: strip failed: " + ex.Message);
            }
        }

        /// <summary>True if <paramref name="assembly"/> was loaded into the game's collectible context.</summary>
        private static bool IsFromUserAlc(Assembly? assembly, AssemblyLoadContext userAlc) =>
            assembly != null && AssemblyLoadContext.GetLoadContext(assembly) == userAlc;

        /// <summary>First field on <paramref name="type"/> whose type is
        /// <see cref="UnhandledExceptionEventHandler"/> — the backing field of a field-like event.</summary>
        private static FieldInfo? FindDelegateField(Type type, BindingFlags flags)
        {
            foreach (FieldInfo f in type.GetFields(flags))
            {
                if (f.FieldType == typeof(UnhandledExceptionEventHandler))
                {
                    return f;
                }
            }
            return null;
        }

        private static void TeardownAudioState()
        {
            try
            {
                // MediaPlayer exposes DisposeIfNecessary as internal — reflection. (It lives in
                // WPR.Framework.Xna as of 5c-3c; the compile-time typeof below rebinds itself, and
                // the member name and its internal-static visibility are load-bearing HERE, so
                // don't rename or re-scope them.) Without this the
                // 'initialized' static stays true across launches and the new FAudio engine
                // ends up with no song subsystem registered, manifesting as silent audio or
                // NREs deep inside the next game's resource loader.
                Type? mp = typeof(Microsoft.Xna.Framework.Media.MediaPlayer);
                MethodInfo? disposeIfNecessary = mp.GetMethod(
                    "DisposeIfNecessary",
                    BindingFlags.NonPublic | BindingFlags.Static);
                disposeIfNecessary?.Invoke(null, null);
            }
            catch (Exception ex)
            {
                Log.Warn(LogCategory.AppList, $"MediaPlayer.DisposeIfNecessary on teardown threw: {ex.Message}");
            }

            try
            {
                // FNA's FAudioContext is a private nested class of SoundEffect with a static
                // 'Context' field. Reach in by reflection — FNA only ever sets it to null inside
                // its own Dispose(), so we have to drive that path here.
                Type? sfx = typeof(Microsoft.Xna.Framework.Audio.SoundEffect);
                Type? ctxType = sfx.GetNestedType("FAudioContext", BindingFlags.NonPublic);
                FieldInfo? ctxField = ctxType?.GetField("Context", BindingFlags.Public | BindingFlags.Static);
                object? ctx = ctxField?.GetValue(null);
                if (ctx != null)
                {
                    MethodInfo? dispose = ctxType!.GetMethod("Dispose", BindingFlags.Public | BindingFlags.Instance);
                    dispose?.Invoke(ctx, null);
                }
            }
            catch (Exception ex)
            {
                Log.Warn(LogCategory.AppList, $"FAudioContext dispose on teardown threw: {ex.Message}");
            }
        }

        /// <summary>
        /// Mirror of the singleton reset that <c>SilverlightLauncher</c> performs on window
        /// close. The XNA path historically skipped this, so the second launch of an XNA game
        /// inherited launch-1's <c>Application.Current</c>, <c>PhoneApplicationService.Current</c>,
        /// and <c>HostContext</c> state — including event subscribers pinned to types in the
        /// previous (unloaded) ALC. Best-effort; any single failure is logged and skipped.
        /// </summary>
        private static void ResetWprSingletons()
        {
            // Application.Current lazy-creates a fresh instance on the next access, so it's
            // safe to clear to null here.
            try { WindowsCompability.Application.ResetCurrent(); }
            catch (Exception ex) { Log.Warn(LogCategory.AppList, $"Application.ResetCurrent threw: {ex.Message}"); }

            try
            {
                // PhoneApplicationService.Current is a plain `=> _Current` getter (no lazy
                // init — the cctor seeds it once per process), so we must REPLACE the
                // singleton with a fresh instance, not null. The new instance's public ctor
                // sets _Current = this internally; we still SetValue afterwards to be
                // explicit. Without this swap, event subscribers added by launch 1 stay
                // wired to types in the unloaded ALC, and any cross-launch handler invoke
                // would touch dead types.
                Type t = typeof(Microsoft.Phone.Shell.PhoneApplicationService);
                FieldInfo? f = t.GetField("_Current", BindingFlags.NonPublic | BindingFlags.Static);
                object? fresh = Activator.CreateInstance(t);
                f?.SetValue(null, fresh);
            }
            catch (Exception ex) { Log.Warn(LogCategory.AppList, $"PhoneApplicationService reset threw: {ex.Message}"); }

            // Clear STATIC EVENTS that game code subscribes to. This is a whole class of ALC leak:
            // WP7 games wire handlers to these process-wide statics and exit without unsubscribing, so
            // each subscriber stays reachable and pins the game's collectible AssemblyLoadContext —
            // which is why the ALC never unloaded and every launch's assemblies/textures/audio stayed
            // resident (progressively slowing the machine over a session). The accelerometer one also
            // left a 60Hz timer running, invoking an ever-growing handler list. Found 2026-08-08.
            // Through the seam, not at a concrete host: which provider is registered is the
            // platform head's business (keyboard emulator on Windows, hardware sensor on
            // Android), and this backend runs under both. Null when no head registered one.
            try { WPR.Sensors.SensorBackend.Provider?.ResetForNewLaunch(); }
            catch (Exception ex) { Log.Warn(LogCategory.AppList, $"Sensor provider reset threw: {ex.Message}"); }

            try { WPR.SilverlightCompability.CompositionTarget.ResetForNewLaunch(); }
            catch (Exception ex) { Log.Warn(LogCategory.AppList, $"CompositionTarget reset threw: {ex.Message}"); }

            try { WPR.SilverlightCompability.Touch.ResetForNewLaunch(); }
            catch (Exception ex) { Log.Warn(LogCategory.AppList, $"Touch reset threw: {ex.Message}"); }

            try { GamerServicesDispatcher.ResetForNewLaunch(); }
            catch (Exception ex) { Log.Warn(LogCategory.AppList, $"GamerServicesDispatcher reset threw: {ex.Message}"); }

            // (SignedInGamer.Reset() also clears its SignedOut subscribers; it is called on launch.)
            try { SignedInGamer.Reset(); }
            catch (Exception ex) { Log.Warn(LogCategory.AppList, $"SignedInGamer reset threw: {ex.Message}"); }

            try
            {
                WPR.SilverlightCompability.HostContext.UserAssembly = null;
                WPR.SilverlightCompability.HostContext.CurrentProductId = null;
                WPR.SilverlightCompability.HostContext.CurrentInstallFolder = null;
            }
            catch (Exception ex) { Log.Warn(LogCategory.AppList, $"HostContext reset threw: {ex.Message}"); }

            try
            {
                // FNA's ContentTypeReaderManager.contentReadersCache is a private static
                // Dictionary<Type, ContentTypeReader> that's never cleared. It caches reader
                // instances keyed by user-assembly Type objects, which pins the previous
                // launch's user ALC alive forever and (if AppDomain.GetAssemblies fallback
                // is hit) lets launch-2's content path see launch-1's reader. FNA exposes
                // ClearTypeCreators which only clears the *creator-func* dict, not this one.
                // Reach in by reflection.
                Type? ctrm = typeof(Microsoft.Xna.Framework.Content.ContentTypeReaderManager);
                FieldInfo? cacheField = ctrm.GetField(
                    "contentReadersCache",
                    BindingFlags.NonPublic | BindingFlags.Static);
                if (cacheField?.GetValue(null) is System.Collections.IDictionary cacheDict)
                {
                    cacheDict.Clear();
                }
            }
            catch (Exception ex) { Log.Warn(LogCategory.AppList, $"ContentTypeReaderManager.contentReadersCache reset threw: {ex.Message}"); }

            // Sibling DLLs (PressPlay.FFWD, similar mini-engines) were loaded into the
            // non-collectible Default ALC and survive across launches with their per-launch
            // managed statics intact. Concrete bug this addresses: FFWD's
            // `Application.quitNextUpdate` is set to true when the user backs out of a game
            // (MainMenu.OnDoQuit → Application.Quit), and the new launch's Application.Update
            // reads it at frame 0 and immediately Exits — the XNA window closes silently with
            // no log line.
            //
            // Reset is restricted to the exact (TypeName, FieldName) pairs in
            // _PerLaunchFlagsToReset. Earlier revisions of this method walked every type in
            // every sibling assembly to also clear collection-typed statics, but that scan
            // implicitly triggered the cctor of every type it touched — and engine types
            // like Fable Coin Golf's CIwAttractor have cctors that build
            // GraphicsDevice-dependent state (a VertexBuffer via FNA3D). At
            // ResetWprSingletons time the GraphicsDevice is already torn down, so the native
            // FNA3D call read freed memory and raised a fatal AccessViolationException that
            // managed try/catch couldn't contain. Only touch types we have hand-verified.
            List<Assembly> siblings;
            lock (_DefaultAlcUserSiblingsLock)
            {
                siblings = new List<Assembly>(_DefaultAlcUserSiblings);
            }

            foreach ((string typeName, string fieldName) in _PerLaunchFlagsToReset)
            {
                ResetNamedStaticFlag(siblings, typeName, fieldName);
            }

            // Per-launch reference-type static REGISTRIES on sibling DLLs that need
            // explicit clearing because:
            //   (a) the DLL was routed to the non-collectible Default ALC, so its
            //       closed generic statics survive the user-ALC unload, AND
            //   (b) the static collection has no Clear / Reset method that the game
            //       itself calls between launches.
            //
            // Concrete bug this addresses: Kinectimals's Resource.dll defines
            //   public class ManagedResource<T> {
            //       private static Dictionary<string, ManagedResource<T>> s_resources = new(OrdinalIgnoreCase);
            //       private ManagedResource(string name, string src) {
            //           // ... no ContainsKey guard:
            //           s_resources.Add(name, this);
            //       }
            //   }
            // and the game re-runs ResourceManager.Init on every MainGame.ctor.
            // Second launch in the same WPR session → "An item with the same key has
            // already been added. Key: BlackTex" from inside Dictionary.Add, before
            // Game.Run() ever fires.
            //
            // Why this is safer than the abandoned "walk every type, clear every
            // collection static" scan from line 678-685 above: we target a known
            // (Assembly, OpenGenericTypeName, FieldName) tuple, so we never touch a
            // type whose cctor depends on a now-torn-down GraphicsDevice. The cctor
            // for ManagedResource<T> is just `new Dictionary<>()`, which is benign.
            foreach (KnownSiblingRegistry reg in _KnownSiblingRegistries)
            {
                ResetSiblingRegistry(siblings, reg);
            }

            // Stop routing default-ALC resolves to this (now-unloading) user ALC.
            _CurrentUserAlc = null;
            _CurrentMainAssemblyName = null;
        }

        /// <summary>
        /// Describes a static collection registry on a sibling DLL that needs to be
        /// cleared between launches. <see cref="OpenGenericTypeName"/> is the open
        /// generic that carries the static field; <see cref="DiscriminatorOpenName"/>
        /// is a related open generic whose closed instantiations we scan for, to
        /// discover what closed types of the registry exist (closed generic
        /// instantiations are not directly enumerable via reflection — we infer
        /// them from field declarations elsewhere in the loaded assemblies).
        /// </summary>
        private sealed record KnownSiblingRegistry(
            string AssemblyName,
            string OpenGenericTypeName,
            string DiscriminatorOpenName,
            string[] FieldNames);

        private static readonly KnownSiblingRegistry[] _KnownSiblingRegistries =
        {
            // Kinectimals (5a3f9c59-1d30-4895-bb76-641bdd959a8c) — same Resource.dll
            // engine library is used by other Frontier Dev. titles, so this resets
            // anything that ships with a `Resource.ManagedResource<T>` shape.
            new KnownSiblingRegistry(
                AssemblyName: "Resource",
                OpenGenericTypeName: "Resource.ManagedResource`1",
                DiscriminatorOpenName: "Resource.ResourceTypeManager`1",
                FieldNames: new[] { "s_resources", "s_aliases" }),
        };

        private static void ResetSiblingRegistry(List<Assembly> siblings, KnownSiblingRegistry reg)
        {
            try
            {
                // CRITICAL: a sibling DLL like Resource.dll can end up loaded into
                // EITHER the user ALC (when Main.dll's metadata is the first thing
                // to reference it — its load goes through alc.Resolving and lands
                // in user ALC) OR the Default ALC (when a content reader / shim
                // does Type.GetType("X, Resource") — the bind starts in Default ALC
                // and our Default.Resolving routes it to Default ALC). It can even
                // be in BOTH (two separate instances, different ALC).
                //
                // The previous version of this method only scanned
                // _DefaultAlcUserSiblings, so a user-ALC-loaded Resource.dll was
                // never cleared. With the user ALC stuck in "failed to unload"
                // state (see the surrounding GC loop's warning), Resource.dll's
                // static dicts persisted across launches and triggered the
                // "duplicate key: BlackTex" cascade — even though my Default-ALC
                // scan logged "assembly not in sibling list — skip" because the
                // DLL wasn't actually a Default-ALC sibling at all.
                //
                // Fix: look across every loaded assembly with the matching name.
                List<Assembly> allLoaded = AppDomain.CurrentDomain.GetAssemblies()
                    .Where(a => string.Equals(a.GetName().Name, reg.AssemblyName, StringComparison.Ordinal))
                    .ToList();
                if (allLoaded.Count == 0)
                {
                    WprTrace($"[wpr-trace] ResetSiblingRegistry({reg.AssemblyName}): no matching assembly loaded in any ALC — skip.");
                    return;
                }

                WprTrace($"[wpr-trace] ResetSiblingRegistry({reg.AssemblyName}): found {allLoaded.Count} loaded instance(s).");
                foreach (Assembly probe in allLoaded)
                {
                    WprTrace($"[wpr-trace] ResetSiblingRegistry({reg.AssemblyName}): instance ALC={AssemblyLoadContext.GetLoadContext(probe)?.Name ?? "?"}, " +
                             $"in-default-siblings={siblings.Contains(probe)}.");
                }

                // Process every instance — each has its own closed-generic
                // instantiations and its own static dicts.
                int grandCleared = 0;
                int grandClosedTs = 0;
                int grandEntries = 0;
                foreach (Assembly asm in allLoaded)
                {
                    Type? openGeneric = asm.GetType(reg.OpenGenericTypeName, throwOnError: false);
                    Type? discriminator = asm.GetType(reg.DiscriminatorOpenName, throwOnError: false);
                    if (openGeneric == null || !openGeneric.IsGenericTypeDefinition)
                    {
                        WprTrace($"[wpr-trace] ResetSiblingRegistry({reg.AssemblyName}): open generic {reg.OpenGenericTypeName} not in this instance — skip.");
                        continue;
                    }
                    if (discriminator == null || !discriminator.IsGenericTypeDefinition)
                    {
                        WprTrace($"[wpr-trace] ResetSiblingRegistry({reg.AssemblyName}): discriminator {reg.DiscriminatorOpenName} not in this instance — skip.");
                        continue;
                    }

                    ClearOneInstance(asm, openGeneric, discriminator, reg, ref grandCleared, ref grandClosedTs, ref grandEntries);
                }

                string summary =
                    $"ResetSiblingRegistry({reg.AssemblyName}): processed {allLoaded.Count} loaded instance(s), " +
                    $"found {grandClosedTs} closed instantiation(s), cleared {grandCleared} collection(s) " +
                    $"holding {grandEntries} total entries.";
                Log.Info(LogCategory.AppList, summary);
                WprTrace("[wpr-trace] " + summary);
                return;
            }
            catch (Exception ex)
            {
                Log.Warn(LogCategory.AppList,
                    $"ResetSiblingRegistry({reg.AssemblyName}) threw: {ex.Message}");
            }
        }

        private static void ClearOneInstance(
            Assembly asm,
            Type openGeneric,
            Type discriminator,
            KnownSiblingRegistry reg,
            ref int grandCleared,
            ref int grandClosedTs,
            ref int grandEntries)
        {
            try
            {

                // Discover closed instantiations of `openGeneric` and `discriminator`.
                // Closed generic instantiations aren't enumerable directly via
                // reflection, so we collect them by walking every place a Type
                // reference can appear in the metadata: base types, interfaces,
                // field types, property types, method return types, parameter
                // types, and (crucially) method body local variable types. Closed
                // Ts can also be nested inside other generics — e.g. a field
                // declared `Dictionary<string, ManagedResource<X>>` — so we
                // recurse into generic arguments via VisitTypeTree.
                //
                // Scan is restricted to assemblies that reference the open
                // generic's assembly, so we don't churn through Avalonia / FNA /
                // CLR types that can't possibly carry these instantiations.
                HashSet<Type> closedTs = new HashSet<Type>();
                Action<Type?> accumulate = type =>
                    VisitTypeTree(type, t =>
                    {
                        if (!t.IsGenericType || t.IsGenericTypeDefinition) return;
                        Type def = t.GetGenericTypeDefinition();
                        if (def != discriminator && def != openGeneric) return;
                        Type[] args = t.GetGenericArguments();
                        if (args.Length == 1 && args[0] != null) closedTs.Add(args[0]);
                    });

                string targetAsmName = asm.GetName().Name ?? reg.AssemblyName;
                List<Assembly> candidateAssemblies = new List<Assembly>();
                foreach (Assembly a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (a == asm) { candidateAssemblies.Add(a); continue; }
                    try
                    {
                        foreach (AssemblyName an in a.GetReferencedAssemblies())
                        {
                            if (string.Equals(an.Name, targetAsmName, StringComparison.Ordinal))
                            {
                                candidateAssemblies.Add(a);
                                break;
                            }
                        }
                    }
                    catch { /* dynamic assembly with no GetReferencedAssemblies — skip */ }
                }

                const BindingFlags AllMembers =
                    BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.Static | BindingFlags.Instance |
                    BindingFlags.DeclaredOnly;

                int scannedTypes = 0;
                foreach (Assembly a in candidateAssemblies)
                {
                    Type[] types;
                    try { types = a.GetTypes(); }
                    catch (ReflectionTypeLoadException tle) { types = tle.Types.Where(t => t != null).ToArray()!; }
                    catch { continue; }

                    foreach (Type t in types)
                    {
                        if (t == null) continue;
                        scannedTypes++;

                        try { accumulate(t.BaseType); } catch { }
                        try { foreach (Type i in t.GetInterfaces()) accumulate(i); } catch { }

                        try
                        {
                            foreach (FieldInfo f in t.GetFields(AllMembers)) accumulate(f.FieldType);
                        }
                        catch { }

                        try
                        {
                            foreach (PropertyInfo p in t.GetProperties(AllMembers)) accumulate(p.PropertyType);
                        }
                        catch { }

                        try
                        {
                            foreach (MethodInfo m in t.GetMethods(AllMembers))
                            {
                                accumulate(m.ReturnType);
                                foreach (ParameterInfo p in m.GetParameters()) accumulate(p.ParameterType);
                                AccumulateMethodBodyLocals(m, accumulate);
                            }
                        }
                        catch { }

                        try
                        {
                            foreach (ConstructorInfo c in t.GetConstructors(AllMembers))
                            {
                                foreach (ParameterInfo p in c.GetParameters()) accumulate(p.ParameterType);
                                AccumulateMethodBodyLocals(c, accumulate);
                            }
                        }
                        catch { }
                    }
                }

                grandClosedTs += closedTs.Count;
                WprTrace($"[wpr-trace] ResetSiblingRegistry({reg.AssemblyName}): instance scan — " +
                         $"{candidateAssemblies.Count} candidate asms / {scannedTypes} types, " +
                         $"{closedTs.Count} closed T(s).");

                foreach (Type T in closedTs)
                {
                    Type closed;
                    try { closed = openGeneric.MakeGenericType(T); }
                    catch (Exception ex)
                    {
                        Log.Warn(LogCategory.AppList,
                            $"ResetSiblingRegistry: MakeGenericType({reg.OpenGenericTypeName}, {T.FullName}) threw: {ex.Message}");
                        continue;
                    }

                    foreach (string fieldName in reg.FieldNames)
                    {
                        try
                        {
                            FieldInfo? f = closed.GetField(fieldName,
                                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                            if (f == null) continue;
                            // GetValue on a static field forces the closed generic's cctor
                            // to run if it hasn't yet. For ManagedResource<T> that just
                            // initialises an empty Dictionary, so the "cctor may need
                            // GraphicsDevice" hazard from the abandoned blanket walk
                            // doesn't apply here.
                            object? value = f.GetValue(null);
                            if (value is System.Collections.IDictionary d)
                            {
                                grandEntries += d.Count;
                                d.Clear();
                                grandCleared++;
                            }
                            else if (value is System.Collections.IList l)
                            {
                                grandEntries += l.Count;
                                l.Clear();
                                grandCleared++;
                            }
                        }
                        catch (Exception ex)
                        {
                            Log.Warn(LogCategory.AppList,
                                $"ResetSiblingRegistry: clear {closed.FullName}.{fieldName} threw: {ex.Message}");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn(LogCategory.AppList,
                    $"ClearOneInstance({reg.AssemblyName}) threw: {ex.Message}");
            }
        }

        /// <summary>
        /// Recursively walks a type's generic arguments and array element types,
        /// invoking <paramref name="action"/> on every type it encounters. Used by
        /// <see cref="ResetSiblingRegistry"/> to discover closed generic
        /// instantiations nested inside other generics (e.g. find
        /// <c>ManagedResource&lt;X&gt;</c> inside
        /// <c>Dictionary&lt;string, ManagedResource&lt;X&gt;&gt;</c>).
        /// </summary>
        private static void VisitTypeTree(Type? t, Action<Type> action, HashSet<Type>? seen = null)
        {
            if (t == null) return;
            seen ??= new HashSet<Type>();
            if (!seen.Add(t)) return;
            try { action(t); } catch { }
            try
            {
                if (t.IsGenericType)
                {
                    foreach (Type arg in t.GetGenericArguments())
                        VisitTypeTree(arg, action, seen);
                }
                if (t.IsArray) VisitTypeTree(t.GetElementType(), action, seen);
            }
            catch { /* dynamic / type-load oddities — best effort */ }
        }

        private static void AccumulateMethodBodyLocals(MethodBase method, Action<Type?> accumulate)
        {
            MethodBody? body;
            try { body = method.GetMethodBody(); }
            catch { return; }
            if (body == null) return;
            try
            {
                foreach (LocalVariableInfo lv in body.LocalVariables) accumulate(lv.LocalType);
            }
            catch { }
        }

        /// <summary>
        /// Allow-list of (TypeName, FieldName) per-launch flags in Default-ALC sibling DLLs
        /// that need explicit reset between launches. Only add fields confirmed to be plain
        /// managed value types (bool/int/etc., not [ThreadStatic], not behind a property
        /// the JIT may have inlined). Bug history:
        ///  - PressPlay.FFWD.Application.quitNextUpdate: Tentacles silent-close on relaunch
        ///    after a back-button quit (Application.Quit set the flag; next Application
        ///    instance read it at frame 0 and Game.Exit'd).
        /// </summary>
        private static readonly (string TypeName, string FieldName)[] _PerLaunchFlagsToReset =
        {
            ("PressPlay.FFWD.Application", "quitNextUpdate"),
        };

        private static void ResetNamedStaticFlag(List<Assembly> siblings, string typeName, string fieldName)
        {
            try
            {
                Type? t = siblings
                    .Select(a => a.GetType(typeName, throwOnError: false, ignoreCase: false))
                    .FirstOrDefault(x => x != null);
                if (t == null) return;
                FieldInfo? f = t.GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);
                if (f == null || f.IsLiteral || f.IsInitOnly || !f.FieldType.IsValueType) return;
                f.SetValue(null, Activator.CreateInstance(f.FieldType));
            }
            catch (Exception ex) { Log.Warn(LogCategory.AppList, $"ResetNamedStaticFlag({typeName}.{fieldName}) threw: {ex.Message}"); }
        }

        private static string BuildDiagnostics(Game? obj, Exception ex)
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("=== WPR Game Diagnostic ===");
            sb.AppendLine("Timestamp: " + DateTime.UtcNow.ToString("o"));
            sb.AppendLine();

            sb.AppendLine("Exception:");
            sb.AppendLine(ex.ToString());
            sb.AppendLine();

            if (obj == null)
            {
                sb.AppendLine("Game instance is null.");
                return sb.ToString();
            }

            try
            {
                Type t = obj.GetType();
                sb.AppendLine($"Game type: {t.FullName}");

                // Common Game properties
                try { sb.AppendLine($"Window: {(obj.Window == null ? "<null>" : obj.Window.GetType().FullName)}"); } catch { sb.AppendLine("Window: <error>"); }
                try { sb.AppendLine($"Window.Title: {(obj.Window?.Title ?? "<null>")}"); } catch { sb.AppendLine("Window.Title: <error>"); }
                try { sb.AppendLine($"GraphicsDevice: {(obj.GraphicsDevice == null ? "<null>" : obj.GraphicsDevice.GetType().FullName)}"); } catch { sb.AppendLine("GraphicsDevice: <error>"); }
                try { sb.AppendLine($"Content: {(obj.Content == null ? "<null>" : obj.Content.GetType().FullName)}"); } catch { sb.AppendLine("Content: <error>"); }
                try { sb.AppendLine($"Services: {(obj.Services == null ? "<null>" : obj.Services.GetType().FullName)}"); } catch { sb.AppendLine("Services: <error>"); }

                sb.AppendLine();

                // Inspect instance fields and properties (best-effort, do not throw)
                sb.AppendLine("Instance fields:");
                foreach (var field in t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                {
                    try
                    {
                        object? val = field.GetValue(obj);
                        sb.AppendLine($"- {field.Name} ({field.FieldType.FullName}): {(val == null ? "<null>" : val.GetType().FullName)}");
                    }
                    catch (Exception fex)
                    {
                        sb.AppendLine($"- {field.Name}: <error reading> {fex.Message}");
                    }
                }

                sb.AppendLine();
                sb.AppendLine("Instance properties:");
                foreach (var prop in t.GetProperties(BindingFlags.Instance | BindingFlags.Public))
                {
                    if (!prop.CanRead) continue;
                    try
                    {
                        object? val = prop.GetValue(obj);
                        sb.AppendLine($"- {prop.Name} ({prop.PropertyType.FullName}): {(val == null ? "<null>" : val.GetType().FullName)}");
                    }
                    catch (Exception pex)
                    {
                        sb.AppendLine($"- {prop.Name}: <error reading> {pex.Message}");
                    }
                }
            }
            catch (Exception outer)
            {
                sb.AppendLine("Failed to build full diagnostics: " + outer);
            }

            return sb.ToString();
        }
    }
}
