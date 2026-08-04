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
    }

    public static class ApplicationInstaller
    {
        private const string TempXmlFile = "temp.xml";
        private static string TempXmlFileFullPath => Configuration.Current!.DataPath(TempXmlFile);

        public static ApplicationPreview? ReadPreview(Stream fileStream)
        {
            try
            {
                if (fileStream.CanSeek) fileStream.Position = 0;
                using ZipArchive archive = new ZipArchive(fileStream, ZipArchiveMode.Read, leaveOpen: true);

                ZipArchiveEntry? manifestEntry = archive.GetEntry("WMAppManifest.xml");
                if (manifestEntry == null) return null;

                XmlDocument manifestDoc = new XmlDocument();
                using (Stream ms = manifestEntry.Open())
                {
                    manifestDoc.Load(ms);
                }

                XmlNode? appNode = manifestDoc.DocumentElement?.SelectSingleNode("//App");
                if (appNode == null) return null;

                XmlAttribute? titleAttrib = appNode.Attributes?["Title"];
                XmlAttribute? versionAttrib = appNode.Attributes?["Version"];
                XmlAttribute? productAttrib = appNode.Attributes?["ProductID"];

                if (titleAttrib == null || versionAttrib == null || productAttrib == null) return null;

                ApplicationPreview preview = new ApplicationPreview
                {
                    Name = titleAttrib.Value,
                    Version = versionAttrib.Value,
                    ProductId = productAttrib.Value.Trim('{').Trim('}'),
                    Author = appNode.Attributes?["Author"]?.Value ?? "Unknown",
                    Publisher = appNode.Attributes?["Publisher"]?.Value ?? "Unknown",
                    Description = appNode.Attributes?["Description"]?.Value ?? "",
                };

                // WP8 manifests spell "Modern Native" with a space that Enum.TryParse
                // rejects, matching the normalisation done during full install below.
                string? runtimeRaw = appNode.Attributes?["RuntimeType"]?.Value;
                if (!string.IsNullOrEmpty(runtimeRaw))
                {
                    string normalized = runtimeRaw.Replace(" ", "");
                    if (Enum.TryParse(normalized, ignoreCase: true, out Models.ApplicationType parsed))
                    {
                        preview.ApplicationType = parsed;
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

        private static async Task<(ApplicationInstallError, Application?, string)> CreateApplicationEntryAndExtract(Stream fileStream, Action<int> progressSet, Func<Application, IObservable<bool>> deleteExistingApp, CancellationToken canceled)
        {
            Application? app = null;
            string dataFolderProduct = "";

            try
            {
                ZipArchive archive = new ZipArchive(fileStream);
                ZipArchiveEntry? entry = archive.GetEntry("WMAppManifest.xml");
                if (entry == null)
                {
                    return ( ApplicationInstallError.MissingManifestFiles, app, dataFolderProduct );
                }

                if (canceled.IsCancellationRequested)
                {
                    return ( ApplicationInstallError.Canceled, app, dataFolderProduct );
                }

                progressSet(3);

                entry.ExtractToFile(TempXmlFileFullPath, true);

                XmlDocument wmManifestDoc = new XmlDocument();
                wmManifestDoc.Load(TempXmlFileFullPath);

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
                if (!Enum.TryParse(normalizedRuntimeType, true, out ApplicationType runtimeTypeParsed))
                {
                    return ( ApplicationInstallError.NotSupportedAppType, app, dataFolderProduct );
                }

                // WP8 "Modern Native" packages (native ARM/C++ against WinRT) have no managed
                // entry assembly to host and ship without the Silverlight AppManifest.xaml.
                // Reject with an honest reason HERE — otherwise the AppManifest.xaml check below
                // fires and misreports it as MissingManifestFiles ("missing manifest files!").
                if (runtimeTypeParsed == ApplicationType.ModernNative)
                {
                    return ( ApplicationInstallError.ModernNativeUnsupported, app, dataFolderProduct );
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

                entry = archive.GetEntry("AppManifest.xaml");
                if (entry == null)
                {
                    return ( ApplicationInstallError.MissingManifestFiles, app, dataFolderProduct) ;
                }

                if (canceled.IsCancellationRequested)
                {
                    return (ApplicationInstallError.Canceled, app, dataFolderProduct);
                }

                entry.ExtractToFile(TempXmlFileFullPath, true);

                wmManifestDoc = new XmlDocument();
                wmManifestDoc.Load(TempXmlFileFullPath);

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

                app = new Application()
                {
                    Name = titleAttrib.Value,
                    ApplicationType = runtimeTypeParsed,
                    Version = versionAttrib.Value,
                    IconPath = (iconPath == null) ? "" : Path.Combine(productStoreFolderRelative, iconPath),
                    Publisher = (publisherAttrib == null) ? "Unknown" : publisherAttrib.Value,
                    Author = (authorAttrib == null) ? "Unknown" : authorAttrib.Value,
                    Description = (descriptionAttrib == null) ? "" : descriptionAttrib.Value,
                    ProductId = productTrimmed,
                    Assembly = entryPointAsmFileNameAttrib.Value,
                    EntryPoint = entryPointTypeAttrib.Value,
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

        public static async Task<ApplicationInstallError> Install(Stream fileStream, Action<int> progressSet, Func<Application, IObservable<bool>> deleteExistingApp, CancellationToken cancelSource)
        {
            try
            {
                Application? app;
                string? appDataFolder;
                ApplicationInstallError error;

                // 60% spend for extracting files
                (error, app, appDataFolder) = await Task.Run(() => CreateApplicationEntryAndExtract(fileStream, progressSet, deleteExistingApp, cancelSource));

                if (error != ApplicationInstallError.None)
                {
                    return error;
                }

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
                catch (Exception exception)
                {
                    Log.Error(LogCategory.AppInstall, $"Application DLL patching failed with exception:\n{exception}");
                    return ApplicationInstallError.PatchFailed;
                }

                if (cancelSource.IsCancellationRequested)
                {
                    Directory.Delete(appDataFolder, true);
                    ApplicationContext.Current.Remove(app);

                    return ApplicationInstallError.Canceled;
                }

                // 20% for converting audio to unified support
                if (app.ApplicationType == ApplicationType.XNA)
                {
                    try
                    {
                        await AudioCompabilityConverter.ScanWmaAndConvert(appDataFolder,
                            progress => progressSet(80 + (int)((double)progress / 5)),
                            cancelSource);
                    } catch (Exception exception)
                    {
                        Log.Error(LogCategory.AppInstall, $"Application WMA conversion failed with exception:\n{exception}");
                        return ApplicationInstallError.ConvertFailed;
                    }
                }

                if (cancelSource.IsCancellationRequested)
                {
                    Directory.Delete(appDataFolder, true);
                    ApplicationContext.Current.Remove(app);

                    return ApplicationInstallError.Canceled;
                }

                progressSet(100);
            } catch (Exception ex)
            {
                Log.Error(LogCategory.AppInstall, $"An unexpected error happen during the installation: \n{ex}");
                return ApplicationInstallError.UnexpectedError;
            }

            return ApplicationInstallError.None;
        }

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
                        progress => progressSet(30 + (int)(progress * 0.7)),
                        cancelToken);
                }, cancelToken);

                if (cancelToken.IsCancellationRequested) return ApplicationInstallError.Canceled;

                // 3) Stamp the new patcher version on the DB row so the UI can tell whether
                //    a reinstall is still needed for any future patcher changes.
                app.PatchedVersion = ApplicationPatcher.Version;
                ApplicationContext.Current.Applications!.Update(app);
                await ApplicationContext.Current.SaveChangesAsync();

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
