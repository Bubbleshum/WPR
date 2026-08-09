using System;
using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Xml;
using WPR.SilverlightCompability;
using Microsoft.Phone.Controls;
using SLApplication = WPR.WindowsCompability.Application;

namespace WPR
{
    /// <summary>
    /// Boots a patched Silverlight user assembly to the point where its RootVisual is ready
    /// to display. Mirrors the WP runtime startup sequence: load assembly, instantiate the
    /// App class (which runs InitializeComponent → LoadComponent), pull RootVisual, navigate
    /// the frame to MainPage if applicable.
    ///
    /// Returns the configured RootVisual; the caller is expected to wrap it in a window.
    /// </summary>
    public static class SilverlightAppHost
    {
        public class HostResult
        {
            public UIElement? RootVisual { get; set; }
            public PhoneApplicationFrame? RootFrame { get; set; }
            public Uri? StartPageUri { get; set; }
            public Assembly? UserAssembly { get; set; }
            public AssemblyLoadContext? LoadContext { get; set; }
        }

        public static HostResult Boot(string installFolder, string assemblyFileName, string entryPointTypeName)
        {
            if (installFolder == null) throw new ArgumentNullException(nameof(installFolder));
            if (assemblyFileName == null) throw new ArgumentNullException(nameof(assemblyFileName));
            if (entryPointTypeName == null) throw new ArgumentNullException(nameof(entryPointTypeName));

            // Some WP titles call ContentManager.Load on Xbox-LIVE / title-specific
            // settings files that should have been part of the OS image rather than
            // the app's XAP. Their XAPs assume the files are present beside the
            // assembly and FATAL with FileNotFoundException at first read. Seed
            // empty defaults so the game's first ContentManager call succeeds; if
            // the app legitimately needs specific keys, it would have shipped the
            // file in the XAP and we wouldn't be in this branch.
            EnsureXnaSettingsFiles(installFolder);

            // Make the install folder available to renderers / asset-loading code paths.
            // Image.GetAvaloniaBitmap and ImageBrush resolution use this to convert
            // app-relative paths like "/Resources/silver.png" into absolute on-disk paths.
            WPR.SilverlightCompability.HostContext.CurrentInstallFolder = installFolder;

            // Also publish it to the neutral ambient holder so low-level shims (e.g.
            // XElement2.Load) can resolve bare relative data reads against the install
            // folder — a Silverlight app runs in-process, so the CWD is the host exe dir.
            WPR.Common.WprHostEnvironment.CurrentInstallFolder = installFolder;

            string normalizedFile = AssemblyNameStandardization.Process(assemblyFileName);
            string asmPath = Path.Combine(installFolder, normalizedFile);
            if (!File.Exists(asmPath))
                throw new FileNotFoundException($"User assembly '{normalizedFile}' not found in '{installFolder}'.", asmPath);

            // Use a collectible context so the user assembly can be unloaded after the app
            // closes — otherwise the .dll stays locked for the life of WPR and reinstall fails.
            var alc = new AssemblyLoadContext(
                $"WPR.SilverlightApp[{Path.GetFileNameWithoutExtension(asmPath)}]",
                isCollectible: true);
            alc.Resolving += (ctx, name) =>
            {
                string candidate = Path.Combine(installFolder, name.Name + ".dll");
                if (File.Exists(candidate)) return ctx.LoadFromAssemblyPath(candidate);

                // .winmd-projected DLLs reference system facades like `mscorlib v=255.255.255.255`.
                // Default ALC strict-binds on version and rejects, so we fall back to a name-only
                // load that picks up whatever the host runtime provides (mscorlib v=4.0 in net8.0).
                try
                {
                    return AssemblyLoadContext.Default.LoadFromAssemblyName(new AssemblyName(name.Name!));
                }
                catch
                {
                    return null;
                }
            };

            Assembly userAsm = alc.LoadFromAssemblyPath(asmPath);
            HostContext.UserAssembly = userAsm;
            Type? appType = userAsm.GetType(entryPointTypeName, throwOnError: false);
            if (appType == null)
                throw new InvalidOperationException(
                    $"Entry-point type '{entryPointTypeName}' not found in '{userAsm.GetName().Name}'.");

            Uri? startPage = TryReadStartPageUri(installFolder);

            // Instantiating the App class triggers its generated InitializeComponent → LoadComponent.
            // The user's App.xaml.cs may set Application.Current.RootVisual itself, OR the App.xaml
            // may set RootVisual via the parsed tree.
            object? appInstance;
            try { appInstance = Activator.CreateInstance(appType); }
            catch (TargetInvocationException tex) when (tex.InnerException != null)
            {
                throw new InvalidOperationException(
                    $"Failed to instantiate '{entryPointTypeName}': {tex.InnerException.Message}", tex.InnerException);
            }
            if (appInstance == null)
                throw new InvalidOperationException($"Activator.CreateInstance returned null for '{entryPointTypeName}'.");

            UIElement? rootVisual = SLApplication.Current.RootVisual;
            PhoneApplicationFrame? rootFrame = rootVisual as PhoneApplicationFrame;

            // The standard WP template does NOT set Application.Current.RootVisual inside the
            // App ctor — it builds a RootFrame, hooks Navigated, and lets WP's framework drive
            // the first navigation; the Navigated handler then assigns RootVisual = RootFrame.
            // Mimic that by finding the user's RootFrame field/property and navigating it.
            if (rootFrame == null)
            {
                rootFrame = FindRootFrameOnApp(appInstance);
            }

            if (rootFrame != null && startPage != null && rootFrame.CurrentSource == null)
            {
                try { rootFrame.Navigate(startPage); }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"Failed to navigate to start page '{startPage}': {ex.Message}", ex);
                }
            }

            // The user's CompleteInitializePhoneApplication handler — wired to Navigated —
            // typically does `RootVisual = RootFrame` on the first navigation. Re-read.
            if (rootVisual == null)
            {
                rootVisual = SLApplication.Current.RootVisual;
            }

            // Backstop: the user's app might not bother setting RootVisual at all (some
            // simplified templates / WinRT-hybrid apps just rely on the framework to assume
            // RootFrame == root). If we have a RootFrame but no RootVisual, host the frame.
            if (rootVisual == null && rootFrame != null)
            {
                SLApplication.Current.RootVisual = rootFrame;
                rootVisual = rootFrame;
            }

            return new HostResult
            {
                RootVisual = rootVisual ?? rootFrame,
                RootFrame = rootFrame,
                StartPageUri = startPage,
                UserAssembly = userAsm,
                LoadContext = alc,
            };
        }

        /// <summary>
        /// Looks for a public/non-public PhoneApplicationFrame property or field on the user's
        /// App — covers the standard WP template's instance
        /// <c>public PhoneApplicationFrame RootFrame { get; private set; }</c> as well as the
        /// <c>static</c> variant some titles use (e.g. AC Pirates' <c>SparkApplicationXaml.App</c>
        /// declares <c>public static PhoneApplicationFrame RootFrame</c>).
        /// </summary>
        private static PhoneApplicationFrame? FindRootFrameOnApp(object? appInstance)
        {
            if (appInstance == null) return null;
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic
                | BindingFlags.Instance | BindingFlags.Static;

            for (Type? t = appInstance.GetType(); t != null && t != typeof(object); t = t.BaseType)
            {
                foreach (var p in t.GetProperties(flags))
                {
                    if (!typeof(PhoneApplicationFrame).IsAssignableFrom(p.PropertyType)) continue;
                    if (p.GetIndexParameters().Length != 0) continue;
                    bool isStatic = p.GetGetMethod(true)?.IsStatic ?? false;
                    try
                    {
                        if (p.GetValue(isStatic ? null : appInstance) is PhoneApplicationFrame found) return found;
                    }
                    catch { /* property may throw before fully constructed; skip */ }
                }
                foreach (var f in t.GetFields(flags))
                {
                    if (!typeof(PhoneApplicationFrame).IsAssignableFrom(f.FieldType)) continue;
                    try
                    {
                        if (f.GetValue(f.IsStatic ? null : appInstance) is PhoneApplicationFrame found) return found;
                    }
                    catch { }
                }
            }
            return null;
        }

        /// <summary>
        /// Writes empty-dictionary defaults for <c>XboxLIVESettings.xml</c> and
        /// <c>TitleSpecificSettings.xml</c> next to the user assembly if they're
        /// missing. WP titles that read these via <c>ContentManager.Load&lt;Dictionary&lt;string,string&gt;&gt;</c>
        /// will get a deserializable empty dictionary back instead of FATALing on
        /// FileNotFoundException — same shape as the real ones, just no entries.
        ///
        /// We never overwrite existing files: if the user has already manually
        /// placed real production settings (the Xbox LIVE service URLs etc.),
        /// those keep priority.
        /// </summary>
        private static void EnsureXnaSettingsFiles(string installFolder)
        {
            // The XNA Content pipeline's XML-content reader for
            // Dictionary&lt;string,string&gt; — an empty dictionary is a valid asset and
            // round-trips cleanly. Any title that needs specific keys would have
            // shipped its own file; we never overwrite.
            const string emptyDict =
                "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
                "<XnaContent xmlns:Generic=\"System.Collections.Generic\">\n" +
                "  <!-- Auto-generated by WPR: empty default to satisfy ContentManager.Load. -->\n" +
                "  <Asset Type=\"Generic:Dictionary[string,string]\">\n" +
                "  </Asset>\n" +
                "</XnaContent>\n";

            string[] names = { "XboxLIVESettings.xml", "TitleSpecificSettings.xml" };
            foreach (string name in names)
            {
                string path = Path.Combine(installFolder, name);
                if (File.Exists(path)) continue;
                try
                {
                    File.WriteAllText(path, emptyDict);
                }
                catch
                {
                    // Best-effort. If the disk is read-only or permissions block us,
                    // we just let the original FileNotFoundException surface at
                    // first ContentManager.Load — the user will see a clearer
                    // failure than a silent partial seed.
                }
            }
        }

        /// <summary>
        /// Reads <c>WMAppManifest.xml</c> from the install folder and returns the start page URI
        /// declared by <c>&lt;DefaultTask NavigationPage="..." /&gt;</c>, or null if absent.
        /// </summary>
        private static Uri? TryReadStartPageUri(string installFolder)
        {
            string manifestPath = Path.Combine(installFolder, "WMAppManifest.xml");
            if (!File.Exists(manifestPath)) return null;

            try
            {
                var doc = new XmlDocument();
                doc.Load(manifestPath);
                XmlNodeList? tasks = doc.GetElementsByTagName("DefaultTask");
                if (tasks == null || tasks.Count == 0) return null;
                XmlAttribute? attr = tasks[0]?.Attributes?["NavigationPage"];
                if (attr == null || string.IsNullOrWhiteSpace(attr.Value)) return null;
                return new Uri("/" + attr.Value.TrimStart('/'), UriKind.Relative);
            }
            catch
            {
                return null;
            }
        }
    }
}
