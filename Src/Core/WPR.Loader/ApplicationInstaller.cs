using Mono.Cecil;
using System;
using System.IO.Compression;
using System.Reflection;
using System.Xml;
using Microsoft.EntityFrameworkCore;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using WPR.Common;
using WPR.Models;
using WPR.Core;

namespace WPR
{
    public class ApplicationPreview
    {
        public string Name { get; set; } = "";
        public string Author { get; set; } = "Unknown";
        public string Publisher { get; set; } = "Unknown";
        public string Description { get; set; } = "";
        public string Version { get; set; } = "";
        public string ProductId { get; set; } = "";
        public byte[]? IconBytes { get; set; }

        /// <summary>
        /// Parsed runtime type from <c>WMAppManifest.xml</c>'s <c>App@RuntimeType</c>
        /// attribute. Null only if the attribute is missing or its value doesn't map
        /// to a known <see cref="Models.ApplicationType"/>. Surfacing this on the
        /// preview lets the desktop UI show "SILVERLIGHT" / "XNA" / "MODERN NATIVE"
        /// in the detail-pane eyebrow for not-yet-installed apps, instead of the
        /// generic "available to install" placeholder.
        /// </summary>
        public Models.ApplicationType? ApplicationType { get; set; }

        /// <summary>
        /// True when the package's WMAppManifest.xml was missing, unreadable or incomplete and
        /// some of this preview was synthesised from its contents (see <see cref="PackageManifests"/>).
        /// </summary>
        public bool MetadataSynthesised { get; set; }
    }

    public static class ApplicationInstaller
    {
        public static ApplicationPreview? ReadPreview(Stream fileStream)
        {
            try
            {
                if (fileStream.CanSeek) fileStream.Position = 0;
                using ZipArchive archive = new ZipArchive(fileStream, ZipArchiveMode.Read, leaveOpen: true);

                // A missing, unreadable or incomplete WMAppManifest.xml is completed from the
                // package's contents (PackageManifests); null only when nothing in it can run.
                PackageManifests? manifests = PackageManifests.Load(archive, PackageNameOf(fileStream), needEntryPoint: false);
                if (manifests == null) return null;
                XmlDocument manifestDoc = manifests.WmAppManifest;

                XmlNode? appNode = manifestDoc.DocumentElement?.SelectSingleNode("//App");
                if (appNode == null) return null;

                XmlAttribute? titleAttrib = appNode.Attributes?["Title"];
                XmlAttribute? versionAttrib = appNode.Attributes?["Version"];
                XmlAttribute? productAttrib = appNode.Attributes?["ProductID"];

                if (titleAttrib == null || versionAttrib == null || productAttrib == null) return null;

                ApplicationPreview preview = new ApplicationPreview
                {
                    Name = AppResourceTitle.Resolve(archive, titleAttrib.Value),
                    // Not versionAttrib.Value directly: half the library never bumped that
                    // attribute off the 1.0.0.0 template default. See ApplicationVersionResolver.
                    Version = ApplicationVersionResolver.Resolve(archive, versionAttrib.Value, titleAttrib.Value),
                    ProductId = productAttrib.Value.Trim('{').Trim('}'),
                    Author = appNode.Attributes?["Author"]?.Value ?? "Unknown",
                    Publisher = appNode.Attributes?["Publisher"]?.Value ?? "Unknown",
                    Description = appNode.Attributes?["Description"]?.Value ?? "",
                    MetadataSynthesised = manifests.IsSynthesised,
                };

                // WP8 manifests spell "Modern Native" with a space that Enum.TryParse
                // rejects, matching the normalisation done during full install below.
                string? runtimeRaw = appNode.Attributes?["RuntimeType"]?.Value;
                if (!string.IsNullOrEmpty(runtimeRaw))
                {
                    string normalized = runtimeRaw.Replace(" ", "");
                    if (Enum.TryParse(normalized, ignoreCase: true, out Models.ApplicationType parsed))
                    {
                        preview.ApplicationType = parsed == Models.ApplicationType.Silverlight &&
                                                  NativeXamlComponent.Find(archive, appNode) != null
                            ? Models.ApplicationType.ModernNative
                            : parsed;
                    }
                }

                XmlNode? iconPathNode = appNode.SelectSingleNode("//IconPath");
                if (iconPathNode != null)
                {
                    string iconPath = iconPathNode.InnerText;
                    ZipArchiveEntry? iconEntry = archive.GetEntry(iconPath);
                    if (iconEntry != null)
                    {
                        using Stream iconStream = iconEntry.Open();
                        using MemoryStream buffer = new MemoryStream();
                        iconStream.CopyTo(buffer);
                        preview.IconBytes = buffer.ToArray();
                    }
                }

                return preview;
            }
            catch (Exception ex)
            {
                Log.Error(LogCategory.AppInstall, $"Failed to read XAP preview: {ex}");
                return null;
            }
        }

        /// <summary>The package's file name, when the stream is a file (the title of last resort).</summary>
        private static string? PackageNameOf(Stream stream) => stream is FileStream fs ? Path.GetFileName(fs.Name) : null;

        /// <param name="onSynthesised">Told the manifests when some of them were synthesised.</param>
        private static async Task<(ApplicationInstallError, Application?, string)> CreateApplicationEntryAndExtract(Stream fileStream, Action<int> progressSet, Func<Application, IObservable<bool>> deleteExistingApp, CancellationToken canceled,
            Action<PackageManifests> onSynthesised)
        {
            Application? app = null;
            string dataFolderProduct = "";

            try
            {
                // A Marketplace download still wrapped in its DRM ("PRE\x07", not "PK\x03\x04")
                // cannot be opened by anything. Say so, rather than an unexpected-error throw.
                if (IsDrmWrapped(fileStream))
                {
                    return ( ApplicationInstallError.NotDecrypted, app, dataFolderProduct );
                }

                ZipArchive archive = new ZipArchive(fileStream);

                // The package's manifests, or - when WMAppManifest.xml / AppManifest.xaml is
                // missing, unreadable or incomplete - ones completed from what the package carries.
                // Refused only when nothing in it can run (every game is attempted).
                PackageManifests? manifests = PackageManifests.Load(archive, PackageNameOf(fileStream));
                if (manifests == null)
                {
                    return ( ApplicationInstallError.MissingManifestFiles, app, dataFolderProduct );
                }
                if (manifests.IsSynthesised)
                {
                    onSynthesised(manifests);
                    Log.Warn(LogCategory.AppInstall,
                        $"Package manifests incomplete ({string.Join(", ", manifests.Synthesised)}); attempting with synthesised metadata: {manifests.Describe()}");
                }

                if (canceled.IsCancellationRequested)
                {
                    return ( ApplicationInstallError.Canceled, app, dataFolderProduct );
                }

                progressSet(3);

                XmlDocument wmManifestDoc = manifests.WmAppManifest;

                XmlElement? root = wmManifestDoc.DocumentElement;
                XmlNodeList? appNodeList = root!.SelectNodes("//App");

                if (appNodeList == null)
                {
                    return ( ApplicationInstallError.InvalidManifestFiles, app, dataFolderProduct );
                }

                XmlNode? appNode = appNodeList[0];

                XmlAttribute? titleAttrib = appNode!.Attributes!["Title"];
                XmlAttribute? runtimeTypeAttrb = appNode!.Attributes!["RuntimeType"];
                XmlAttribute? versionAttrib = appNode!.Attributes!["Version"];
                XmlAttribute? productAttrib = appNode!.Attributes!["ProductID"];

                if ((titleAttrib == null) || (runtimeTypeAttrb == null) || (versionAttrib == null) || (productAttrib == null))
                {
                    return ( ApplicationInstallError.InvalidManifestFiles, app, dataFolderProduct );
                }

                string productTrimmed = productAttrib!.Value.Trim('{').Trim('}');
                string storeFolder = Configuration.Current!.DataPath(Application.DataStoreFolder);
                string productStoreFolder = Path.Combine(storeFolder, productTrimmed);
                string productStoreFolderRelative = Path.Combine(Application.DataStoreFolder, productTrimmed);

                List<Application> existingApp = await ApplicationContext.Current.Applications!
                    .Where(a => a.ProductId == productTrimmed)
                    .ToListAsync();

                if (existingApp.Count != 0)
                {
                    if (await deleteExistingApp(existingApp[0]))
                    {
                        if (Directory.Exists(productStoreFolder))
                        {
                            await DeleteInstallFolderAsync(productStoreFolder);
                        }
                        ApplicationContext.Current.Applications!.Remove(existingApp[0]);
                        await ApplicationContext.Current.SaveChangesAsync();
                    } else
                    {
                        return ( ApplicationInstallError.Canceled, app, dataFolderProduct );
                    }
                }
                else if (Directory.Exists(productStoreFolder))
                {
                    // No DB record but the folder is present — orphaned from a previous
                    // half-uninstall (e.g. Mirror's Edge: the user removed the DB row but a
                    // locked DLL prevented the folder from being deleted at the time).
                    // Clear it now so the extract below doesn't trip over a leftover file
                    // that's been freed since.
                    await DeleteInstallFolderAsync(productStoreFolder);
                }

                // Normalize whitespace: WP8 manifests use values like "Modern Native" with a space
                // that Enum.TryParse won't accept. Map to the equivalent enum identifier.
                string normalizedRuntimeType = runtimeTypeAttrb.Value.Replace(" ", "");
                if (!Enum.TryParse(normalizedRuntimeType, true, out ApplicationType runtimeTypeParsed)
                    || !Enum.IsDefined(runtimeTypeParsed) || runtimeTypeParsed == ApplicationType.UnityPort)
                {
                    // A RuntimeType WPR has never seen is not a reason to refuse the game: every
                    // package is attempted, and one that fails reports to WPR Hub from the run.
                    // Guess from what the package carries: a native default task means a native
                    // title; anything else goes to SilverlightGame, which hosts both a Silverlight
                    // page and a SharedGraphicsDeviceManager XNA game.
                    string? task = (appNode!.SelectSingleNode("//DefaultTask") as XmlElement)?.GetAttribute("ImagePath");
                    runtimeTypeParsed = task != null && task.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                        ? ApplicationType.ModernNative
                        : ApplicationType.Silverlight;
                    Log.Warn(LogCategory.AppInstall,
                        $"Unknown RuntimeType '{runtimeTypeAttrb.Value}'; attempting it as {runtimeTypeParsed}.");
                }

                // WP8 "Modern Native" packages (native ARM/C++ against WinRT) have no managed
                // entry assembly to host and ship without the Silverlight AppManifest.xaml.
                // Reject with an honest reason HERE — otherwise the AppManifest.xaml check below
                // fires and misreports it as MissingManifestFiles ("missing manifest files!").
                // They also have no managed entry point: what runs is the ARM executable named by
                // the default task, hosted by WPR.Engine.Wp8Native on the dynarmic JIT.
                // A Direct3D/XAML title: "Silverlight" in the manifest, a native ARM component in
                // fact. It installs and runs as a native title, with the component as its image.
                string? xamlComponent = runtimeTypeParsed == ApplicationType.Silverlight
                    ? NativeXamlComponent.Find(archive, appNode!)
                    : null;
                if (xamlComponent != null)
                {
                    runtimeTypeParsed = ApplicationType.ModernNative;
                }

                bool isNative = runtimeTypeParsed == ApplicationType.ModernNative;
                string? nativeImage = xamlComponent;
                if (isNative && nativeImage == null)
                {
                    nativeImage = (appNode!.SelectSingleNode("//DefaultTask") as XmlElement)?.GetAttribute("ImagePath");
                    if (string.IsNullOrWhiteSpace(nativeImage) || archive.GetEntry(nativeImage) == null)
                    {
                        // The default task names no image in the package. Rather than refuse,
                        // try the package's one executable if it has exactly one: the WP8 host
                        // will answer what it can and report the rest to WPR Hub.
                        List<ZipArchiveEntry> exes = archive.Entries
                            .Where(e => e.FullName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                            .ToList();
                        nativeImage = exes.Count == 1 ? exes[0].FullName : null;
                    }

                    if (nativeImage == null)
                    {
                        // Nothing to execute at all: a real refusal (reported by Install).
                        return ( ApplicationInstallError.ModernNativeUnsupported, app, dataFolderProduct );
                    }
                }

                XmlAttribute? authorAttrib = appNode!.Attributes!["Author"];
                XmlAttribute? publisherAttrib = appNode!.Attributes!["Publisher"];
                XmlAttribute? descriptionAttrib = appNode!.Attributes!["Description"];

                XmlNodeList? iconPathNodes = appNode!.SelectNodes("//IconPath");
                String? iconPath = null;

                if (iconPathNodes != null)
                {
                    iconPath = iconPathNodes[0]!.InnerText;
                }

                string entryAssembly;
                string entryType;
                if (isNative)
                {
                    entryAssembly = nativeImage!;
                    entryType = string.Empty;
                }
                else
                {
                    if (manifests.AppManifest == null)
                    {
                        // No usable AppManifest.xaml and no managed entry point found by inspection.
                        return ( ApplicationInstallError.MissingManifestFiles, app, dataFolderProduct) ;
                    }

                    if (canceled.IsCancellationRequested)
                    {
                        return (ApplicationInstallError.Canceled, app, dataFolderProduct);
                    }

                    wmManifestDoc = manifests.AppManifest;

                    XmlNode? deploymentNode = wmManifestDoc.DocumentElement;

                    XmlAttribute? entryPointAsmAttrib = deploymentNode!.Attributes!["EntryPointAssembly"];
                    XmlAttribute? entryPointTypeAttrib = deploymentNode!.Attributes!["EntryPointType"];
                
                    if ((entryPointAsmAttrib == null) || (entryPointTypeAttrib == null))
                    {
                        return (ApplicationInstallError.InvalidManifestFiles, app, dataFolderProduct);
                    }

                    var nsmgr = new XmlNamespaceManager(wmManifestDoc.NameTable);
                    nsmgr.AddNamespace("a", "http://schemas.microsoft.com/client/2007/deployment");

                    XmlNodeList? assemblies = deploymentNode!.SelectNodes("//a:Deployment.Parts//a:AssemblyPart", nsmgr);
                    if (assemblies == null)
                    {
                        return (ApplicationInstallError.InvalidManifestFiles, app, dataFolderProduct);
                    }

                    XmlAttribute? entryPointAsmFileNameAttrib = null;

                    foreach (XmlNode? assembly in assemblies)
                    {
                        XmlAttribute? attrib = assembly!.Attributes!["x:Name"] ?? assembly!.Attributes!["Name"];
                        if ((attrib == null) || (attrib.Value != entryPointAsmAttrib.Value))
                        {
                            continue;
                        }
                        entryPointAsmFileNameAttrib = assembly!.Attributes!["Source"];
                    }

                    if (entryPointAsmFileNameAttrib == null)
                    {
                        return (ApplicationInstallError.InvalidManifestFiles, app, dataFolderProduct);
                    }

                    entryAssembly = entryPointAsmFileNameAttrib.Value;
                    entryType = entryPointTypeAttrib.Value;
                }

                progressSet(5);

                dataFolderProduct = productStoreFolder;

                Directory.CreateDirectory(productStoreFolder);
                int count = 0;

                foreach (ZipArchiveEntry iterateEntry in archive.Entries)
                {
                    if (canceled.IsCancellationRequested)
                    {
                        Directory.Delete(productStoreFolder, true);
                        return (ApplicationInstallError.Canceled, app, dataFolderProduct);
                    }
                    string fileDestinationPath = Path.Combine(productStoreFolder, iterateEntry.FullName);

                    if (Path.GetFileName(fileDestinationPath).Length == 0)
                    {
                        Directory.CreateDirectory(fileDestinationPath);
                    }
                    else
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(fileDestinationPath)!);
                        iterateEntry.ExtractToFile(fileDestinationPath, true);
                    }

                    count++;
                    progressSet(5 + (int)((float)count / archive.Entries.Count * 50.0f));
                }

                // The WP8 runtime and the Silverlight host read WMAppManifest.xml (ProductID,
                // start page, components) from the install folder, so they see what was installed.
                if (manifests.IsSynthesised)
                {
                    try { manifests.WriteInto(productStoreFolder); }
                    catch (Exception ex) { Log.Warn(LogCategory.AppInstall, $"Could not write the synthesised manifests (non-fatal): {ex.Message}"); }
                }

                app = new Application()
                {
                    Name = AppResourceTitle.Resolve(archive, titleAttrib.Value),
                    ApplicationType = runtimeTypeParsed,
                    // See ApplicationVersionResolver: the manifest attribute is the placeholder
                    // 1.0.0.0 for half the library, so the entry assembly is consulted when it is.
                    Version = ApplicationVersionResolver.Resolve(archive, versionAttrib.Value, titleAttrib.Value),
                    IconPath = (iconPath == null) ? "" : Path.Combine(productStoreFolderRelative, iconPath),
                    Publisher = (publisherAttrib == null) ? "Unknown" : publisherAttrib.Value,
                    Author = (authorAttrib == null) ? "Unknown" : authorAttrib.Value,
                    Description = (descriptionAttrib == null) ? "" : descriptionAttrib.Value,
                    ProductId = productTrimmed,
                    Assembly = entryAssembly,
                    EntryPoint = entryType,
                    InstalledTime = DateTime.Now,
                    PatchedVersion = ApplicationPatcher.Version
                };

                ApplicationContext.Current.Add(app);
                ApplicationContext.Current.SaveChanges();

                progressSet(60);
            }
            catch (Exception ex)
            {
                Log.Error(LogCategory.AppInstall, $"An unexpected error happen during the installation: \n{ex}");
                return (ApplicationInstallError.UnexpectedError, app, dataFolderProduct);
            }

            return (ApplicationInstallError.None, app, dataFolderProduct);
        }

        /// <param name="packageSource">Where the package can be found again later, recorded for
        /// WPR Hub uploads (<see cref="WPR.Engine.Online.GamePackageRecord.Source"/>): a file path
        /// on the desktop, a persisted <c>content://</c> URI on Android, or null.</param>
        public static async Task<ApplicationInstallError> Install(Stream fileStream, Action<int> progressSet, Func<Application, IObservable<bool>> deleteExistingApp, CancellationToken cancelSource,
            string? packageSource = null)
        {
            try
            {
                Application? app;
                string? appDataFolder;
                ApplicationInstallError error;

                // The package's SHA-256, taken now because this is the only time we hold the
                // original bytes: the install folder is about to be full of patched assemblies.
                // Crash reports name the build by it. Best effort - an install never fails for it.
                WPR.Engine.Online.GamePackageRecord? package = null;
                try
                {
                    package = await Task.Run(() => WPR.Engine.Online.GamePackageRecord.Measure(fileStream));
                    if (package != null) package.Source = packageSource;
                }
                catch (Exception ex)
                {
                    Log.Warn(LogCategory.AppInstall, $"Could not fingerprint the package (non-fatal): {ex.Message}");
                }

                // 60% spend for extracting files
                PackageManifests? synthesised = null;
                (error, app, appDataFolder) = await Task.Run(() => CreateApplicationEntryAndExtract(fileStream, progressSet, deleteExistingApp, cancelSource,
                    m => synthesised = m));

                if (error != ApplicationInstallError.None)
                {
                    if (error != ApplicationInstallError.Canceled)
                    {
                        ReportRefusedPackage(fileStream, error, package);
                    }
                    return error;
                }

                // Installed from synthesised metadata: say so on WPR Hub (an install problem,
                // grouped by what was missing), so the case stays diagnosable.
                if (synthesised != null)
                {
                    ReportSynthesisedManifests(synthesised, app!, package, appDataFolder);
                }

                // A WP8 native title is ARM machine code: there is no IL to stub, patch or
                // rescope, and its audio is its own business. It is extracted and recorded only.
                if (app!.ApplicationType == ApplicationType.ModernNative)
                {
                    // Native titles have nothing to patch, but a committed catalogue under
                    // Database/Achievements/<productId>/ still names their achievements. Without
                    // one, rows are created as the game unlocks them (Wp8NativeXboxHost).
                    try { await XnaAchievementSeeder.SeedAsync(app.ProductId ?? "", app.Name ?? ""); }
                    catch (Exception ex)
                    {
                        Log.Warn(LogCategory.AppInstall, $"XnaAchievementSeeder failed (non-fatal): {ex.Message}");
                    }
                }
                else
                {
                    // Pre-patch: replace any WinRT .winmd / native .dll pairs with managed stubs
                    // so the user's IL can JIT against managed types. Best-effort — failures are
                    // logged but don't fail the install (a hybrid app may still partially work).
                    try
                    {
                        await Task.Run(() =>
                        {
                            WinmdStubber.StubInPlace(appDataFolder);
                            WindowsTypeSynthesizer.SynthesizeIfNeeded(appDataFolder);
                            // After everything's stubbed, scrub WinRT content-type flag from every
                            // asm ref. The user's own DLLs (compiled against .winmd) carry that
                            // flag and the JIT throws PNS when it encounters one on net8.0.
                            WinRtRefStripper.StripInPlace(appDataFolder);
                            // GameMaker Studio apps: read-only scan of game.win for achievement
                            // metadata, persist into AchievementContext so they show up in WPR's
                            // UI. Doesn't modify game.win — that's important for the patcher pass
                            // below, which uses the untouched original as input. No-op for non-GMS.
                            GameMakerAchievementExtractor.ExtractInPlace(
                                appDataFolder,
                                app?.ProductId ?? "",
                                app?.Name ?? "");

                            // XNA / GamerServices apps: seed AchievementContext from the
                            // game's committed hardcoded catalogue (Database/Achievements/
                            // <productId>/), all entries marked locked. Lets the WPR UI show
                            // the full achievement list immediately, and — more importantly —
                            // guarantees rows exist by the time the game calls AwardAchievement,
                            // which only flips IsEarned on rows that are already present. No-op
                            // for games without a catalogue. Best-effort; we block on the task
                            // so the install pipeline completes deterministically.
                            try { XnaAchievementSeeder.SeedAsync(app?.ProductId ?? "", app?.Name ?? "").GetAwaiter().GetResult(); }
                            catch (Exception ex)
                            {
                                Log.Warn(LogCategory.AppInstall, $"XnaAchievementSeeder failed (non-fatal): {ex.Message}");
                            }

                            // Surgical bytecode neutralization: produces game.win.patched as a
                            // sibling. One specific script (achievements_add) gets its body
                            // replaced with a single Exit opcode so games like Briquid Mini boot
                            // past the WP-specific FATAL where god.PreCreate calls
                            // achievements_add before achievements_define has run. NO compiler
                            // invocation, NO other scripts touched — minimizes the chance of
                            // breaking variable scope info on Runner 2.1.4.200.
                            // Original game.win is never modified; the launcher prefers .patched
                            // when present, falls back to original when absent.
                            GameMakerWinPatcher.PatchInPlace(appDataFolder);
                        });
                    }
                    catch (Exception exception)
                    {
                        Log.Warn(LogCategory.AppInstall, $"WinRT stubbing failed (non-fatal):\n{exception}");
                    }

                    // 20% for patching DLLs
                    try
                    {
                        await Task.Run(() =>
                        {
                            ApplicationPatcher patcher = new ApplicationPatcher();
                            patcher.Patch(appDataFolder, progress => progressSet(60 + (int)((double)progress / 5)), cancelSource);
                        });
                    }
                    catch (Exception exception) when (exception is not OperationCanceledException)
                    {
                        // Not a reason to refuse the game. Most of the patcher works per assembly
                        // and already skips one it cannot write, so an exception here usually
                        // leaves a partly patched folder that may well run; the run's own reports
                        // say what broke. PatchedVersion 0 marks it stale, so Android repatches on
                        // the next launch and the desktop offers Repatch.
                        Log.Error(LogCategory.AppInstall, $"Application DLL patching failed with exception (installing anyway):\n{exception}");
                        app!.PatchedVersion = 0;
                        try { ApplicationContext.Current.SaveChanges(); } catch (Exception) { /* the row is already saved */ }

                        WPR.Engine.Online.OnlineBackend.Crashes?.Report(WPR.Engine.Online.CrashReport.ForInstallProblem(
                            "install", nameof(ApplicationInstallError.PatchFailed), exception,
                            app.ProductId, app.Name, package, appDataFolder, "installed anyway"));
                    }
                    catch (OperationCanceledException)
                    {
                        // Handled by the cancellation check straight below.
                    }

                    if (cancelSource.IsCancellationRequested)
                    {
                        Directory.Delete(appDataFolder, true);
                        ApplicationContext.Current.Remove(app);

                        return ApplicationInstallError.Canceled;
                    }

                    // 20% for converting audio to unified support.
                    //
                    // NOT gated on ApplicationType any more. It used to run only for ApplicationType.XNA,
                    // on the reasoning that a Silverlight app does not use XNA's MediaPlayer — but a
                    // Silverlight/XNA MIXED-MODE title does, and all twelve of them in the library
                    // declare RuntimeType="Silverlight", so every one had its soundtrack skipped at
                    // install AND at repatch. Carcassonne is the measured case, and the cost was not
                    // merely silence: MediaPlayer skips a song whose container it cannot decode and
                    // reports it as playing, so the queue ends it immediately, the game's
                    // MediaStateChanged handler starts the next track, and its PlayNextSong queues a
                    // fresh ThreadPool work item every frame. Those race their own playlist index and
                    // one eventually throws IndexOutOfRange on a thread-pool thread — unhandled, so
                    // the process dies several minutes in with nothing in the log to connect it to
                    // audio.
                    //
                    // Safe to widen: ScanWmaAndConvert returns immediately when the package has no
                    // .wma at all (most Silverlight titles), and skips any .wma with no .xnb Song stub
                    // beside it, so a title that merely ships one as a loose asset is untouched.
                    {
                        try
                        {
                            // Task.Run for the same reason the patch step above uses it: this is called
                            // from the UI thread in both heads, and the work before the converter's
                            // first await (an external-storage directory walk, plus loading the
                            // transcoder's native library) is enough to stutter the progress dialog on
                            // its own. Inside Task.Run there is no SynchronizationContext to capture, so
                            // nothing in the conversion loop can be posted back to the UI thread.
                            await Task.Run(() => AudioCompabilityConverter.ScanWmaAndConvert(appDataFolder,
                                progress => progressSet(80 + (int)((double)progress / 5)),
                                cancelSource));
                        }
                        catch (OperationCanceledException)
                        {
                            // Handled by the cancellation check below.
                        }
                        catch (Exception exception)
                        {
                            // This used to fail the install, so that a broken transcoder could not
                            // produce a game that installs cleanly and plays no music. It now
                            // installs the game anyway - every game is attempted - and tells WPR
                            // Hub, which is where a silently mute game would otherwise go unseen.
                            // The row was already saved, so the old early return also left a
                            // half-installed game behind an error dialog.
                            Log.Error(LogCategory.AppInstall, $"Application WMA conversion failed (installing anyway, songs may be silent):\n{exception}");
                            WPR.Engine.Online.OnlineBackend.Crashes?.Report(WPR.Engine.Online.CrashReport.ForInstallProblem(
                                "install", nameof(ApplicationInstallError.ConvertFailed), exception,
                                app.ProductId, app.Name, package, appDataFolder, "installed anyway"));
                        }
                    }

                    // Movies, where this platform needs them rewritten (Android: MediaCodec has no
                    // WMV). Never fails the install: a movie that cannot be prepared is skipped at
                    // run time, as every movie used to be.
                    try
                    {
                        await Task.Run(() => VideoCompabilityConverter.PrepareMovies(appDataFolder, _ => { }, cancelSource));
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (Exception exception)
                    {
                        Log.Warn(LogCategory.AppInstall, $"Movie preparation failed (cutscenes will be skipped): {exception.Message}");
                    }

                    if (cancelSource.IsCancellationRequested)
                    {
                        Directory.Delete(appDataFolder, true);
                        ApplicationContext.Current.Remove(app);

                        return ApplicationInstallError.Canceled;
                    }

                    // Keep a copy of the tile icon in the app's own data store: the achievements page
                    // outlives the install folder the original lives in. See GameIconStore. Done here
                    // rather than beside the DB insert so a cancelled install — which deletes the
                    // folder and the row again above — leaves nothing cached behind.
                }

                GameIconStore.Capture(app);

                if (package != null)
                {
                    try
                    {
                        package.Save(appDataFolder!);

                        // Tell WPR Hub which build was just installed, so it can ask for the
                        // package if it has never seen it. Queued only (no-op with server logging
                        // off); the head's flush after install sends it and runs the upload.
                        WPR.Engine.Online.OnlineBackend.Crashes?.Report(WPR.Engine.Online.CrashReport.ForInstall(
                            app!.ProductId, app.Name, appDataFolder!,
                            // Every in-process title runs on the XNA host now, Silverlight included.
                            WPR.Engine.Online.CrashReport.RuntimePaths.XnaFna));
                    }
                    catch (Exception ex) { Log.Warn(LogCategory.AppInstall, $"Could not write {WPR.Engine.Online.GamePackageRecord.FileName} (non-fatal): {ex.Message}"); }
                }

                progressSet(100);
            } catch (Exception ex)
            {
                Log.Error(LogCategory.AppInstall, $"An unexpected error happen during the installation: \n{ex}");
                ReportRefusedPackage(fileStream, ApplicationInstallError.UnexpectedError);
                return ApplicationInstallError.UnexpectedError;
            }

            return ApplicationInstallError.None;
        }

        /// <summary>
        /// Tells WPR Hub about a package WPR would not install (no manifest, a native package with
        /// nothing to execute, ...), so the hub learns which titles people try. Queued through the
        /// crash reporter, so it is a no-op with server logging off, and carries the package's
        /// fingerprint so the hub can ask for a build it has never seen. Never throws.
        /// </summary>
        /// <param name="package">Already measured, when the caller has it; otherwise measured here.</param>
        public static void ReportRefusedPackage(Stream? fileStream, ApplicationInstallError error,
            WPR.Engine.Online.GamePackageRecord? package = null, string? packageSource = null)
        {
            try
            {
                if (WPR.Engine.Online.OnlineBackend.Crashes is not { } reporter) return;

                // A caller that could not read the manifest does not know why; the commonest
                // reason by far is a package still wrapped in Marketplace DRM.
                if (error is ApplicationInstallError.InvalidManifestFiles or ApplicationInstallError.MissingManifestFiles
                    && fileStream != null && IsDrmWrapped(fileStream))
                {
                    error = ApplicationInstallError.NotDecrypted;
                }

                if (package == null && fileStream is { CanSeek: true })
                {
                    package = WPR.Engine.Online.GamePackageRecord.Measure(fileStream);
                    if (package != null) package.Source = packageSource;
                }

                ApplicationPreview? identity = null;
                if (error != ApplicationInstallError.NotDecrypted && fileStream is { CanSeek: true, CanRead: true })
                {
                    identity = ReadPreview(fileStream);
                }

                WPR.Engine.Online.CrashReport report = WPR.Engine.Online.CrashReport.ForRefusal(
                    "install", error.ToString(), DescribeRefusal(error),
                    string.IsNullOrEmpty(identity?.ProductId) ? null : identity!.ProductId,
                    identity?.Name, package);
                if (identity?.ApplicationType is { } type) report.Extra["runtime_type"] = type.ToString();
                reporter.Report(report);
                Log.Info(LogCategory.AppInstall, $"Refused package reported to WPR Hub: {error} ({identity?.Name ?? package?.PackageFileName ?? "unnamed"})");
            }
            catch (Exception ex)
            {
                Log.Warn(LogCategory.AppInstall, $"Could not report a refused package: {ex.Message}");
            }
        }

        private static void ReportSynthesisedManifests(PackageManifests manifests, Application app,
            WPR.Engine.Online.GamePackageRecord? package, string? installFolder)
        {
            try
            {
                // The message is the stable list of what was missing, so the hub groups these by
                // cause; the values and where each came from travel in Extra.
                var problem = new InvalidDataException("package manifests synthesised: " + string.Join(", ", manifests.Synthesised));
                WPR.Engine.Online.CrashReport report = WPR.Engine.Online.CrashReport.ForInstallProblem(
                    "install", "ManifestSynthesised", problem,
                    app.ProductId, app.Name, package, installFolder, "installed with synthesised metadata");
                report.Extra["synthesised"] = string.Join(",", manifests.Synthesised);
                report.Extra["synthesis_detail"] = manifests.Describe();
                report.Extra["runtime_type"] = app.ApplicationType.ToString();
                WPR.Engine.Online.OnlineBackend.Crashes?.Report(report);
            }
            catch (Exception ex)
            {
                Log.Warn(LogCategory.AppInstall, $"Could not report synthesised manifests: {ex.Message}");
            }
        }

        /// <summary>True for a package that starts <c>PRE\x07</c>: a Marketplace download still in its DRM wrapper.</summary>
        private static bool IsDrmWrapped(Stream stream)
        {
            if (!stream.CanSeek || !stream.CanRead) return false;
            long position = stream.Position;
            try
            {
                stream.Position = 0;
                Span<byte> magic = stackalloc byte[4];
                return stream.Read(magic) == 4 && magic[0] == (byte)'P' && magic[1] == (byte)'R' && magic[2] == (byte)'E' && magic[3] == 7;
            }
            catch (Exception)
            {
                return false;
            }
            finally
            {
                try { stream.Position = position; } catch (Exception) { /* best effort */ }
            }
        }

        private static string DescribeRefusal(ApplicationInstallError error) => error switch
        {
            ApplicationInstallError.NotDecrypted => "The package is a Marketplace download still wrapped in its DRM (starts PRE\\x07, not a ZIP).",
            // Manifests are synthesised from the contents when missing or broken (PackageManifests),
            // so these mean nothing in the package could be run, or it is not a readable package.
            ApplicationInstallError.MissingManifestFiles => "The package's manifests are missing or unusable and nothing runnable was found in it (no XNA Game, no Silverlight Application, no ARM executable).",
            ApplicationInstallError.InvalidManifestFiles => "The package could not be read as a XAP, or its manifests are unusable and nothing runnable was found in it.",
            ApplicationInstallError.ModernNativeUnsupported => "A WP8 native package with no executable image to run.",
            ApplicationInstallError.NotSupportedAppType => "The package's runtime type is not one WPR can host.",
            ApplicationInstallError.ConvertFailed => "The install-time audio transcoder failed for every track.",
            ApplicationInstallError.UnexpectedError => "The installer threw; see the session log.",
            _ => error.ToString(),
        };

        /// <summary>
        /// Remove the DB row for <paramref name="app"/> and best-effort delete its install
        /// folder under <c>%LocalAppData%\WPR\AppData\&lt;ProductId&gt;</c>. The folder
        /// delete is wrapped in <see cref="DeleteInstallFolderAsync"/> which retries through
        /// transient locks. If the folder can't be deleted (a previous launch leaked an
        /// AssemblyLoadContext that's still pinning a DLL) we leave it on disk — the next
        /// install will clear leftover files itself (see <see cref="CreateApplicationEntryAndExtract"/>).
        /// </summary>
        public static async Task<bool> UninstallAsync(Application app)
        {
            if (app == null) return false;

            string productTrimmed = app.ProductId.Trim('{').Trim('}');
            string installFolder = Path.Combine(
                Configuration.Current!.DataPath(Application.DataStoreFolder),
                productTrimmed);

            // Last chance to keep the tile icon: it lives inside the folder about to go, and the
            // achievements list still shows this game afterwards. Also the backfill for anything
            // installed before GameIconStore existed — a no-op once the install path has captured.
            GameIconStore.Capture(app);

            bool folderGone = true;
            if (Directory.Exists(installFolder))
            {
                folderGone = await DeleteInstallFolderAsync(installFolder);
                if (!folderGone)
                {
                    Log.Warn(LogCategory.AppInstall,
                        $"Uninstall: install folder for {app.Name} ({app.ProductId}) could " +
                        $"not be deleted (files still locked). The DB row will still be removed; " +
                        $"the next install will clean the folder.");
                }
            }

            ApplicationContext.Current.Applications!.Remove(app);
            await ApplicationContext.Current.SaveChangesAsync();
            return folderGone;
        }

        /// <summary>
        /// Re-run the patcher on an already-installed app without re-extracting from the XAP.
        /// Walks the install dir restoring <c>*.dll</c> from each <c>*.dll.original</c> sidecar
        /// (the patcher writes these on first install — see <see cref="ApplicationPatcher.PatchDll"/>),
        /// then invokes <see cref="ApplicationPatcher.Patch"/> which produces fresh <c>.original</c>
        /// files from the restored DLLs and overwrites the in-place copies with newly-patched IL.
        /// Updates <see cref="Application.PatchedVersion"/> on success.
        /// </summary>
        public static async Task<ApplicationInstallError> RepatchAsync(
            Application app,
            Action<int> progressSet,
            CancellationToken cancelToken)
        {
            // A WP8 native title is ARM code, never patched at install: nothing to redo.
            if (app.ApplicationType == ApplicationType.ModernNative)
            {
                progressSet(100);
                return ApplicationInstallError.None;
            }

            try
            {
                string productTrimmed = app.ProductId.Trim('{').Trim('}');
                string installFolder = Path.Combine(
                    Configuration.Current!.DataPath(Application.DataStoreFolder),
                    productTrimmed);

                if (!Directory.Exists(installFolder))
                {
                    Log.Error(LogCategory.AppInstall,
                        $"Repatch: install folder missing for {app.Name} ({app.ProductId}): {installFolder}");
                    return ApplicationInstallError.UnexpectedError;
                }

                progressSet(0);

                // 1) Restore every patched DLL from its .original sidecar. If there's no
                //    sidecar, the file wasn't patched (e.g. it was a passthrough or got
                //    added after install) — leave it alone. After this pass the install
                //    dir is in the same state it was right after extract.
                await Task.Run(() =>
                {
                    string[] originals = Directory.GetFiles(installFolder, "*.dll.original", SearchOption.AllDirectories);
                    int total = originals.Length;
                    int done = 0;
                    foreach (string original in originals)
                    {
                        if (cancelToken.IsCancellationRequested) return;
                        string dll = original.Substring(0, original.Length - ".original".Length);
                        try
                        {
                            File.Move(original, dll, overwrite: true);
                        }
                        catch (Exception ex)
                        {
                            Log.Warn(LogCategory.AppInstall,
                                $"Repatch: failed to restore {Path.GetFileName(dll)} from .original: {ex.Message}");
                        }
                        done++;
                        progressSet(total == 0 ? 30 : (int)(done * 30.0 / total));
                    }
                }, cancelToken);

                if (cancelToken.IsCancellationRequested) return ApplicationInstallError.Canceled;

                // 2) Re-run the patcher. It writes new .original sidecars from the restored
                //    inputs and overwrites the in-place DLLs with newly-patched IL.
                await Task.Run(() =>
                {
                    ApplicationPatcher patcher = new ApplicationPatcher();
                    patcher.Patch(
                        installFolder,
                        progress => progressSet(30 + (int)(progress * 0.5)),
                        cancelToken);
                }, cancelToken);

                if (cancelToken.IsCancellationRequested) return ApplicationInstallError.Canceled;

                // 2b) Re-run the .wma -> Ogg Vorbis transcode. Added 2026-08-31, when the transcode
                //     moved behind IAudioTranscoder and Android gained a working implementation for
                //     the first time: without this, every XNA game already installed on Android is
                //     mute and the ONLY way to get its soundtrack is a full uninstall + reinstall.
                //     A repatch is far cheaper and the Android head already exposes one
                //     (GamesActivity), so it is worth doing here.
                //
                //     Idempotent, and cheap when there is nothing to do: the converter sniffs each
                //     file's container and skips anything that is already Ogg, so a repatch of an
                //     install whose audio is fine does no work beyond the directory walk.
                //
                //     NON-FATAL, unlike the install path. Repatch's contract is "re-apply the
                //     current patcher", and that has already succeeded by this point; a head with
                //     no transcoder composed in should not lose its IL repatch over the audio.
                //
                //     Like the install path, NO LONGER gated on ApplicationType.XNA — mixed-mode
                //     titles declare RuntimeType="Silverlight" and still use XNA's MediaPlayer.
                //     That gate is why a repatch could not rescue their audio either. See the long
                //     note at the install call site.
                {
                    try
                    {
                        // Task.Run: see the matching call in Install — keeps the whole conversion
                        // off the caller's UI thread.
                        await Task.Run(() => AudioCompabilityConverter.ScanWmaAndConvert(
                            installFolder,
                            progress => progressSet(80 + (int)(progress * 0.2)),
                            cancelToken));
                    }
                    catch (Exception ex)
                    {
                        Log.Warn(LogCategory.AppInstall,
                            $"Repatch: .wma transcode failed for {app.Name} (repatch itself " +
                            $"succeeded; the game's music will be silent): {ex.Message}");
                    }

                    // Movies, as at install. Idempotent, so a repatch is also how an install made
                    // before this existed gets its cutscenes.
                    try
                    {
                        await Task.Run(() => VideoCompabilityConverter.PrepareMovies(installFolder, _ => { }, cancelToken));
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (Exception ex)
                    {
                        Log.Warn(LogCategory.AppInstall, $"Repatch: movie preparation failed for {app.Name}: {ex.Message}");
                    }
                }

                if (cancelToken.IsCancellationRequested) return ApplicationInstallError.Canceled;

                // 3) Stamp the new patcher version on the DB row so the UI can tell whether
                //    a reinstall is still needed for any future patcher changes.
                app.PatchedVersion = ApplicationPatcher.Version;

                //    Also take the chance to correct a placeholder version. Games installed
                //    before ApplicationVersionResolver existed recorded WMAppManifest's
                //    App@Version verbatim, which is 1.0.0.0 for about half the library. The
                //    entry assembly is right here on disk, so a repatch can fix that without
                //    the user re-extracting the XAP. Reads the patched DLL, which is fine —
                //    patching preserves both version sources.
                app.Version = ApplicationVersionResolver.ResolveFromInstallFolder(
                    installFolder, app.Assembly, app.Version, app.Name);
                ApplicationContext.Current.Applications!.Update(app);
                await ApplicationContext.Current.SaveChangesAsync();

                //    And cache the tile icon, for the same reason the transcode re-runs here:
                //    it backfills every game installed before GameIconStore existed, in bulk via
                //    --repatch-installed, rather than waiting for each one to be uninstalled.
                GameIconStore.Capture(app);

                progressSet(100);
                return ApplicationInstallError.None;
            }
            catch (Exception ex)
            {
                Log.Error(LogCategory.AppInstall, $"Repatch failed for {app.Name}: {ex}");
                return ApplicationInstallError.PatchFailed;
            }
        }

        /// <summary>
        /// Best-effort recursive delete with retries. Each pass calls <see cref="GC.Collect"/>
        /// before retrying — that runs finalizers and releases any leaked AssemblyLoadContext
        /// file handles that a previous game launch may have left holding a .dll open. Returns
        /// true if the folder is gone (or was already missing); false if it's still there
        /// after every retry.
        /// </summary>
        private static async Task<bool> DeleteInstallFolderAsync(string folder)
        {
            if (!Directory.Exists(folder)) return true;

            // 5 attempts × 200ms ≈ 1 second of polling. Past that, the lock is almost
            // certainly held by something we can't influence (an external scanner, a
            // long-running file watcher, the user's own AV) and waiting longer won't help.
            int[] waits = { 0, 100, 200, 400, 800 };
            for (int i = 0; i < waits.Length; i++)
            {
                if (waits[i] > 0) await Task.Delay(waits[i]);
                if (i > 0)
                {
                    // Force the previous launch's ALC to actually unload — finalizers free
                    // mapped file handles.
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                }
                try
                {
                    Directory.Delete(folder, recursive: true);
                    return true;
                }
                catch (IOException) when (i < waits.Length - 1) { /* retry */ }
                catch (UnauthorizedAccessException) when (i < waits.Length - 1) { /* retry */ }
                catch (Exception ex)
                {
                    Log.Warn(LogCategory.AppInstall, $"Directory.Delete('{folder}') attempt {i + 1}: {ex.Message}");
                    if (i == waits.Length - 1) return false;
                }
            }
            return !Directory.Exists(folder);
        }
    }
}
