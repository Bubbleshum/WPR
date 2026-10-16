using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using Mono.Cecil;
using Mono.Cecil.Cil;

using WPR.Common;

namespace WPR
{
    /// <summary>
    /// The two manifests an install reads - <c>WMAppManifest.xml</c> (identity, runtime type, start
    /// page) and <c>AppManifest.xaml</c> (managed entry point) - taken from the package when they are
    /// there and whole, and otherwise completed or synthesised from what the package carries.
    /// </summary>
    /// <remarks>
    /// <para>Every game is attempted: a package with no manifest, an unparseable one, or one missing
    /// attributes used to be refused (and, with no manifest, silently left out of the desktop
    /// library). It is now installed whenever something in it can run, and refused only when
    /// nothing can. What can run is decided by inspection:</para>
    /// <list type="bullet">
    /// <item>XNA: a managed type whose base chain reaches <c>Microsoft.Xna.Framework.Game</c>;</item>
    /// <item>Silverlight (pure or mixed-mode): one reaching <c>System.Windows.Application</c>, with a
    /// start page found from a <c>PhoneApplicationPage</c>'s <c>InitializeComponent</c> URI;</item>
    /// <item>WP8 native: an ARM PE executable (machine 0x01C4, no CLR header), or the one a WP8.1
    /// <c>AppxManifest.xml</c> names.</item>
    /// </list>
    /// <para>Identity comes from the manifest where it has it, then <c>AppxManifest.xml</c>
    /// (<c>PhoneProductId</c>, <c>DisplayName</c>), then the entry assembly (<c>[Guid]</c>,
    /// <c>AssemblyTitle</c>/<c>Product</c>, versions, company), then the package file name; a
    /// ProductId of last resort is a GUID made from the SHA-256 of the entry image, so the same
    /// package always installs under the same id (the library scan compares ids).</para>
    /// <para><see cref="Synthesised"/> lists what was made up. The installer writes the completed
    /// manifests into the install folder (keeping the package's own as <c>.original</c>), since the
    /// WP8 runtime and the Silverlight host read <c>WMAppManifest.xml</c> from there, and reports
    /// the list to WPR Hub as an install problem so the case stays diagnosable.</para>
    /// </remarks>
    public sealed class PackageManifests
    {
        public const string WmAppManifestName = "WMAppManifest.xml";
        public const string AppManifestName = "AppManifest.xaml";
        public const string AppxManifestName = "AppxManifest.xml";
        private const string DeploymentNs = "http://schemas.microsoft.com/client/2007/deployment";

        /// <summary>The (possibly completed) WMAppManifest. Always has an <c>App</c> node carrying
        /// Title, RuntimeType, Version and ProductID.</summary>
        public XmlDocument WmAppManifest { get; private set; } = new XmlDocument();

        /// <summary>The (possibly synthesised) AppManifest.xaml, or null when the package has no
        /// usable one and no managed entry point could be found (a native title needs none).</summary>
        public XmlDocument? AppManifest { get; private set; }

        /// <summary>True when <see cref="WmAppManifest"/> is not the package's file byte for byte.</summary>
        public bool WmAppManifestSynthesised { get; private set; }

        /// <summary>True when <see cref="AppManifest"/> is not the package's own.</summary>
        public bool AppManifestSynthesised { get; private set; }

        /// <summary>Stable keys of what was synthesised, e.g. <c>wmappmanifest-missing</c>.
        /// Empty when both manifests came from the package unchanged.</summary>
        public List<string> Synthesised { get; } = new();

        /// <summary>Human-readable detail: which value came from where.</summary>
        public List<string> Details { get; } = new();

        public bool IsSynthesised => Synthesised.Count > 0;

        public string Describe() => string.Join("; ", Details);

        /// <summary>
        /// Reads the package's manifests, completing or synthesising what is missing.
        /// </summary>
        /// <param name="archive">The opened package.</param>
        /// <param name="packageName">The package file name (title of last resort), or null.</param>
        /// <param name="needEntryPoint">False for a preview: only the WMAppManifest identity is
        /// wanted, so a whole manifest is taken as is and AppManifest.xaml is not examined.</param>
        /// <returns>Null when the package has no usable identity and nothing in it can run - the
        /// one case that is still a refusal.</returns>
        public static PackageManifests? Load(ZipArchive archive, string? packageName, bool needEntryPoint = true)
        {
            PackageManifests result = new PackageManifests();

            // ---- WMAppManifest.xml ------------------------------------------------------------
            ZipArchiveEntry? wmEntry = FindEntry(archive, WmAppManifestName);
            XmlDocument? wm = null;
            if (wmEntry == null)
            {
                result.Note("wmappmanifest-missing", "the package has no WMAppManifest.xml");
            }
            else
            {
                try
                {
                    wm = LoadXml(wmEntry);
                }
                catch (Exception ex)
                {
                    result.Note("wmappmanifest-invalid", $"WMAppManifest.xml is not readable XML ({ex.GetType().Name}: {ex.Message})");
                }
            }

            XmlElement? app = wm?.DocumentElement?.SelectSingleNode("//App") as XmlElement;
            if (wm != null && app == null)
            {
                result.Note("wmappmanifest-invalid", "WMAppManifest.xml has no App element");
                wm = null;
            }

            string[] required = { "Title", "RuntimeType", "Version", "ProductID" };
            List<string> missing = app == null
                ? required.ToList()
                : required.Where(a => string.IsNullOrWhiteSpace(app.GetAttribute(a))).ToList();
            if (app != null && missing.Count > 0)
            {
                result.Note("wmappmanifest-incomplete", "WMAppManifest.xml App has no " + string.Join("/", missing));
            }

            // ---- AppManifest.xaml -------------------------------------------------------------
            XmlDocument? appManifest = null;
            string? appManifestProblem = null;
            bool examineAppManifest = needEntryPoint || missing.Count > 0;
            if (examineAppManifest)
            {
                ZipArchiveEntry? amEntry = FindEntry(archive, AppManifestName);
                if (amEntry == null)
                {
                    appManifestProblem = "the package has no AppManifest.xaml";
                }
                else
                {
                    try
                    {
                        appManifest = LoadXml(amEntry);
                        if (ResolveEntryAssemblyFile(appManifest, archive) == null
                            || string.IsNullOrWhiteSpace(appManifest.DocumentElement?.GetAttribute("EntryPointType")))
                        {
                            appManifestProblem = "AppManifest.xaml names no entry assembly in the package, or no entry type";
                            appManifest = null;
                        }
                    }
                    catch (Exception ex)
                    {
                        appManifestProblem = $"AppManifest.xaml is not readable XML ({ex.GetType().Name}: {ex.Message})";
                    }
                }
            }

            // Fast path: everything the installer reads is in the package.
            if (missing.Count == 0 && (!examineAppManifest || appManifestProblem == null))
            {
                result.WmAppManifest = wm!;
                result.AppManifest = appManifest;
                return result;
            }

            // A native title (manifest says so) needs no AppManifest.xaml: leave it to the
            // installer, which finds the image from the default task.
            string declaredRuntime = app?.GetAttribute("RuntimeType") ?? "";
            if (missing.Count == 0 && declaredRuntime.Replace(" ", "").Equals("ModernNative", StringComparison.OrdinalIgnoreCase))
            {
                result.WmAppManifest = wm!;
                return result;
            }

            // ---- Inspect the package ----------------------------------------------------------
            PackageInspection inspection = PackageInspection.Inspect(archive, appManifest, declaredRuntime);

            if (appManifest == null && inspection.EntryAssemblyFile != null && inspection.EntryType != null)
            {
                appManifest = BuildAppManifest(inspection);
                result.AppManifestSynthesised = true;
                result.Note(appManifestProblem != null && appManifestProblem.Contains("no AppManifest") ? "appmanifest-missing" : "appmanifest-invalid",
                    $"{appManifestProblem}; entry point synthesised as {inspection.EntryType} in {inspection.EntryAssemblyFile}");
            }
            else if (appManifest == null && appManifestProblem != null && inspection.Runtime != InspectedRuntime.Native)
            {
                // Nothing managed to point at; the installer reports it if it needs one.
                result.Details.Add(appManifestProblem + "; no managed entry point found");
            }

            if (missing.Count == 0)
            {
                result.WmAppManifest = wm!;
                result.AppManifest = appManifest;
                return result;
            }

            // The WMAppManifest needs completing. Without a runtime type or an identity, and with
            // nothing that runs, there is nothing to attempt.
            if (inspection.Runtime == InspectedRuntime.None && missing.Contains("RuntimeType"))
            {
                result.Details.Add("nothing runnable found: no XNA Game, no Silverlight Application, no ARM executable");
                return null;
            }

            AppxIdentity? appx = AppxIdentity.Read(archive);

            if (wm == null)
            {
                wm = new XmlDocument();
                wm.AppendChild(wm.CreateComment(" Synthesised by WPR: the package's WMAppManifest.xml was missing or unreadable. "));
                XmlElement deployment = wm.CreateElement("Deployment");
                deployment.SetAttribute("AppPlatformVersion", inspection.Runtime == InspectedRuntime.Native ? "8.0" : "7.1");
                wm.AppendChild(deployment);
                app = wm.CreateElement("App");
                deployment.AppendChild(app);
                result.WmAppManifestSynthesised = true;
            }
            else
            {
                wm.DocumentElement!.PrependChild(wm.CreateComment(" Completed by WPR: some App attributes were missing. "));
                result.WmAppManifestSynthesised = true;
            }

            XmlElement appNode = app!;

            void Fill(string attribute, string? value, string source)
            {
                if (!string.IsNullOrWhiteSpace(appNode.GetAttribute(attribute)) || string.IsNullOrWhiteSpace(value)) return;
                appNode.SetAttribute(attribute, value);
                result.Details.Add($"{attribute}='{value}' from {source}");
            }

            // Identity.
            if (!string.IsNullOrWhiteSpace(appx?.ProductId)) Fill("ProductID", Braced(appx!.ProductId), "AppxManifest PhoneProductId");
            if (inspection.AssemblyGuid != null) Fill("ProductID", Braced(inspection.AssemblyGuid), $"[Guid] of {inspection.EntryAssemblyFile}");
            if (inspection.EntryImageHash != null) Fill("ProductID", Braced(GuidFromHash(inspection.EntryImageHash).ToString()), $"SHA-256 of {inspection.EntryImageFile}");

            if (appx != null && !IsResourceString(appx.DisplayName)) Fill("Title", appx.DisplayName, "AppxManifest DisplayName");
            Fill("Title", inspection.Title, $"assembly title of {inspection.EntryAssemblyFile}");
            Fill("Title", CleanPackageName(packageName), "the package file name");
            Fill("Title", inspection.EntryAssemblyName ?? inspection.EntryImageFile, "the entry image name");

            if (appx != null) Fill("Version", appx.Version, "AppxManifest Identity");
            Fill("Version", inspection.Version, $"version of {inspection.EntryAssemblyFile}");
            Fill("Version", "1.0.0.0", "the default");

            Fill("RuntimeType", inspection.Runtime switch
            {
                InspectedRuntime.Xna => "XNA",
                InspectedRuntime.Silverlight => "Silverlight",
                InspectedRuntime.Native => "Modern Native",
                _ => null,
            }, inspection.RuntimeEvidence ?? "inspection");

            if (appx != null) Fill("Publisher", appx.Publisher, "AppxManifest");
            Fill("Author", inspection.Company, "AssemblyCompany");
            Fill("Publisher", inspection.Company, "AssemblyCompany");
            Fill("Description", inspection.Description, "AssemblyDescription");

            if (missing.Contains("ProductID") || missing.Contains("Title") || missing.Contains("RuntimeType") || missing.Contains("Version"))
            {
                // Still nothing to name it by (cannot happen while an image exists, but never
                // hand the installer an App without the four attributes it requires).
                if (required.Any(a => string.IsNullOrWhiteSpace(appNode.GetAttribute(a))))
                {
                    result.Details.Add("could not synthesise " + string.Join("/", required.Where(a => string.IsNullOrWhiteSpace(appNode.GetAttribute(a)))));
                    return null;
                }
            }

            // Icon, start page / image and native components only for a synthesised manifest:
            // a merely incomplete one keeps its own elements.
            if (wmEntry == null || result.Synthesised.Contains("wmappmanifest-invalid"))
            {
                string? icon = inspection.IconPath ?? appx?.Logo;
                if (icon != null)
                {
                    XmlElement iconPath = wm.CreateElement("IconPath");
                    iconPath.SetAttribute("IsRelative", "true");
                    iconPath.SetAttribute("IsResource", "false");
                    iconPath.InnerText = icon;
                    appNode.AppendChild(iconPath);
                    result.Details.Add($"icon '{icon}'");
                }

                XmlElement tasks = wm.CreateElement("Tasks");
                XmlElement task = wm.CreateElement("DefaultTask");
                task.SetAttribute("Name", "_default");
                if (inspection.Runtime == InspectedRuntime.Native && inspection.EntryImageFile != null)
                {
                    task.SetAttribute("ImagePath", inspection.EntryImageFile);
                    result.Details.Add($"native image '{inspection.EntryImageFile}'");
                }
                else if (inspection.StartPage != null)
                {
                    task.SetAttribute("NavigationPage", inspection.StartPage);
                    result.Details.Add($"start page '{inspection.StartPage}'");
                }
                tasks.AppendChild(task);
                appNode.AppendChild(tasks);

                if (inspection.NativeComponents.Count > 0)
                {
                    XmlElement classes = wm.CreateElement("ActivatableClasses");
                    foreach (string component in inspection.NativeComponents)
                    {
                        XmlElement server = wm.CreateElement("InProcessServer");
                        XmlElement path = wm.CreateElement("Path");
                        path.InnerText = component;
                        server.AppendChild(path);
                        classes.AppendChild(server);
                    }
                    appNode.AppendChild(classes);
                    result.Details.Add("native components " + string.Join(", ", inspection.NativeComponents));
                }
            }

            result.WmAppManifest = wm;
            result.AppManifest = appManifest;
            return result;
        }

        private void Note(string key, string detail)
        {
            if (!Synthesised.Contains(key)) Synthesised.Add(key);
            Details.Add(detail);
        }

        /// <summary>Writes the completed manifests into an extracted install folder, keeping a
        /// package file they replace as <c>&lt;name&gt;.original</c>.</summary>
        public void WriteInto(string installFolder)
        {
            if (WmAppManifestSynthesised) Write(installFolder, WmAppManifestName, WmAppManifest);
            if (AppManifestSynthesised && AppManifest != null) Write(installFolder, AppManifestName, AppManifest);
        }

        private static void Write(string folder, string name, XmlDocument doc)
        {
            string path = Path.Combine(folder, name);
            string? existing = Directory.Exists(folder)
                ? Directory.EnumerateFiles(folder).FirstOrDefault(f => Path.GetFileName(f).Equals(name, StringComparison.OrdinalIgnoreCase))
                : null;
            if (existing != null)
            {
                File.Copy(existing, existing + ".original", overwrite: true);
                if (!string.Equals(existing, path, StringComparison.Ordinal)) File.Delete(existing);
            }
            doc.Save(path);
        }

        private static XmlDocument BuildAppManifest(PackageInspection inspection)
        {
            XmlDocument doc = new XmlDocument();
            doc.AppendChild(doc.CreateComment(" Synthesised by WPR: the package's AppManifest.xaml was missing or unusable. "));
            XmlElement deployment = doc.CreateElement("Deployment", DeploymentNs);
            deployment.SetAttribute("EntryPointAssembly", inspection.EntryAssemblyName);
            deployment.SetAttribute("EntryPointType", inspection.EntryType);
            deployment.SetAttribute("RuntimeVersion", "3.0.40624.0");
            doc.AppendChild(deployment);
            XmlElement parts = doc.CreateElement("Deployment.Parts", DeploymentNs);
            deployment.AppendChild(parts);
            foreach ((string name, string file) in inspection.ManagedAssemblies)
            {
                XmlElement part = doc.CreateElement("AssemblyPart", DeploymentNs);
                part.SetAttribute("Name", name);
                part.SetAttribute("Source", file);
                parts.AppendChild(part);
            }
            return doc;
        }

        /// <summary>The Source of the AssemblyPart named by EntryPointAssembly, if that file is in the package.</summary>
        internal static string? ResolveEntryAssemblyFile(XmlDocument appManifest, ZipArchive archive)
        {
            XmlElement? deployment = appManifest.DocumentElement;
            string? entryName = deployment?.GetAttribute("EntryPointAssembly");
            if (string.IsNullOrWhiteSpace(entryName)) return null;

            XmlNamespaceManager ns = new XmlNamespaceManager(appManifest.NameTable);
            ns.AddNamespace("a", DeploymentNs);
            XmlNodeList? parts = deployment!.SelectNodes("//a:Deployment.Parts//a:AssemblyPart", ns);
            if (parts == null) return null;
            foreach (XmlNode part in parts)
            {
                string? name = (part.Attributes?["x:Name"] ?? part.Attributes?["Name"])?.Value;
                string? source = part.Attributes?["Source"]?.Value;
                if (name == entryName && source != null && FindEntry(archive, source) != null) return source;
            }
            return null;
        }

        internal static ZipArchiveEntry? FindEntry(ZipArchive archive, string name)
            => archive.GetEntry(name)
               ?? archive.Entries.FirstOrDefault(e => e.FullName.Replace('\\', '/').Equals(name.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase));

        private static XmlDocument LoadXml(ZipArchiveEntry entry)
        {
            XmlDocument doc = new XmlDocument();
            using Stream stream = entry.Open();
            doc.Load(stream);
            return doc;
        }

        private static string Braced(string guid) => "{" + guid.Trim().Trim('{', '}') + "}";

        private static bool IsResourceString(string? value)
            => value == null || value.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase) || value.StartsWith("@");

        private static string? CleanPackageName(string? packageName)
        {
            if (string.IsNullOrWhiteSpace(packageName)) return null;
            string name = Path.GetFileNameWithoutExtension(packageName.Trim());
            return name.Length == 0 ? null : name;
        }

        /// <summary>A name-based GUID (RFC 4122 version 5 layout) over a SHA-256.</summary>
        internal static Guid GuidFromHash(byte[] sha256)
        {
            byte[] bytes = new byte[16];
            Array.Copy(sha256, bytes, 16);
            bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);
            bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
            return new Guid(bytes);
        }
    }

    internal enum InspectedRuntime { None, Xna, Silverlight, Native }

    /// <summary>What a package can run, read from its assemblies and executables.</summary>
    internal sealed class PackageInspection
    {
        private const string XnaGame = "Microsoft.Xna.Framework.Game";
        private const string SilverlightApplication = "System.Windows.Application";
        private const string PhonePage = "Microsoft.Phone.Controls.PhoneApplicationPage";
        private const long MaxAssemblyBytes = 96L * 1024 * 1024;

        public InspectedRuntime Runtime;
        public string? RuntimeEvidence;
        public string? EntryAssemblyFile;
        public string? EntryAssemblyName;
        public string? EntryType;
        public string? EntryImageFile;
        public byte[]? EntryImageHash;
        public string? AssemblyGuid;
        public string? Title;
        public string? Version;
        public string? Company;
        public string? Description;
        public string? StartPage;
        public string? IconPath;
        public List<string> NativeComponents = new();
        public List<(string Name, string File)> ManagedAssemblies = new();

        private static readonly string[] IconCandidates =
        {
            "ApplicationIcon.png", "Assets/ApplicationIcon.png", "Background.png", "Assets/Background.png",
            "Assets/Tiles/IconicTileSmall.png", "Assets/Tiles/FlipCycleTileSmall.png", "Assets/Logo.png",
        };

        private sealed class Module
        {
            public string File = "";
            public ModuleDefinition Definition = null!;
        }

        public static PackageInspection Inspect(ZipArchive archive, XmlDocument? appManifest, string declaredRuntime)
        {
            PackageInspection inspection = new PackageInspection();
            List<Module> modules = new();
            List<(string File, byte[] Hash)> nativeExes = new();
            List<string> nativeDlls = new();

            foreach (ZipArchiveEntry entry in archive.Entries.OrderBy(e => e.FullName.Count(c => c == '/')).ThenBy(e => e.FullName, StringComparer.OrdinalIgnoreCase))
            {
                bool isDll = entry.FullName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
                bool isExe = entry.FullName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
                if ((!isDll && !isExe) || entry.Length == 0 || entry.Length > MaxAssemblyBytes) continue;

                byte[] bytes;
                try
                {
                    using Stream s = entry.Open();
                    using MemoryStream ms = new MemoryStream((int)entry.Length);
                    s.CopyTo(ms);
                    bytes = ms.ToArray();
                }
                catch (Exception)
                {
                    continue;
                }

                PeKind kind = ClassifyPe(bytes);
                if (kind == PeKind.NativeArm)
                {
                    if (isExe) nativeExes.Add((entry.FullName, SHA256.HashData(bytes)));
                    else nativeDlls.Add(entry.FullName);
                    continue;
                }
                if (kind != PeKind.Managed) continue;

                try
                {
                    ModuleDefinition module = ModuleDefinition.ReadModule(new MemoryStream(bytes),
                        new ReaderParameters { ReadingMode = ReadingMode.Deferred, ReadSymbols = false, AssemblyResolver = NullResolver.Instance });
                    modules.Add(new Module { File = entry.FullName, Definition = module });
                    if (module.Assembly != null)
                    {
                        inspection.ManagedAssemblies.Add((module.Assembly.Name.Name, entry.FullName));
                    }
                }
                catch (Exception)
                {
                    // Not readable as managed metadata: not ours to run.
                }
            }

            // Every type by full name, across the package, so a base class in an engine library
            // (Game1 : Engine.GameBase : Microsoft.Xna.Framework.Game) is followed.
            Dictionary<string, TypeDefinition> types = new(StringComparer.Ordinal);
            foreach (Module m in modules)
            {
                foreach (TypeDefinition t in AllTypes(m.Definition))
                {
                    types.TryAdd(t.FullName, t);
                }
            }

            bool DerivesFrom(TypeDefinition type, string root)
            {
                TypeReference? baseRef = type.BaseType;
                for (int depth = 0; baseRef != null && depth < 32; depth++)
                {
                    string name = baseRef.FullName;
                    if (name == root) return true;
                    if (!types.TryGetValue(name, out TypeDefinition? next)) return false;
                    baseRef = next.BaseType;
                }
                return false;
            }

            // Assemblies another package assembly references are libraries; the entry is on top.
            HashSet<string> referenced = new(StringComparer.OrdinalIgnoreCase);
            foreach (Module m in modules)
            {
                foreach (AssemblyNameReference r in m.Definition.AssemblyReferences) referenced.Add(r.Name);
            }

            List<(Module Module, TypeDefinition Type)> Candidates(string root)
            {
                List<(Module, TypeDefinition)> found = modules
                    .Where(m => !IsFrameworkAssembly(m.Definition.Assembly?.Name.Name))
                    .SelectMany(m => AllTypes(m.Definition).Where(t => t.IsClass && !t.IsAbstract && DerivesFrom(t, root)).Select(t => (m, t)))
                    .ToList();
                // Most derived first: drop a candidate another candidate derives from.
                HashSet<string> bases = new(found.Select(f => f.Item2.BaseType?.FullName ?? ""), StringComparer.Ordinal);
                return found
                    .OrderBy(f => bases.Contains(f.Item2.FullName) ? 1 : 0)
                    .ThenBy(f => referenced.Contains(f.Item1.Definition.Assembly?.Name.Name ?? "") ? 1 : 0)
                    .ThenBy(f => f.Item1.File, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(f => f.Item2.FullName, StringComparer.Ordinal)
                    .ToList();
            }

            Module? entryModule = null;
            TypeDefinition? entryType = null;

            // An AppManifest.xaml that names its entry point decides the managed side.
            if (appManifest != null)
            {
                string? file = PackageManifests.ResolveEntryAssemblyFile(appManifest, archive);
                string? typeName = appManifest.DocumentElement?.GetAttribute("EntryPointType");
                entryModule = modules.FirstOrDefault(m => string.Equals(m.File, file, StringComparison.OrdinalIgnoreCase)
                                                          || string.Equals(PackageManifests.FindEntry(archive, file ?? "")?.FullName, m.File, StringComparison.OrdinalIgnoreCase));
                entryType = typeName == null ? null : entryModule?.Definition.GetType(typeName);
                if (entryType != null)
                {
                    inspection.Runtime = DerivesFrom(entryType, XnaGame) ? InspectedRuntime.Xna : InspectedRuntime.Silverlight;
                    inspection.RuntimeEvidence = $"AppManifest.xaml entry type {typeName}";
                }
            }

            if (entryType == null)
            {
                bool preferXna = declaredRuntime.Equals("XNA", StringComparison.OrdinalIgnoreCase);
                var apps = Candidates(SilverlightApplication);
                var games = Candidates(XnaGame);
                var pick = preferXna ? (games.Count > 0 ? games : apps) : (apps.Count > 0 ? apps : games);
                if (pick.Count > 0)
                {
                    (entryModule, entryType) = pick[0];
                    bool xna = DerivesFrom(entryType, XnaGame);
                    inspection.Runtime = xna ? InspectedRuntime.Xna : InspectedRuntime.Silverlight;
                    inspection.RuntimeEvidence = $"{entryType.FullName} derives from {(xna ? XnaGame : SilverlightApplication)}";
                }
            }

            if (entryType != null && entryModule != null)
            {
                inspection.EntryAssemblyFile = entryModule.File;
                inspection.EntryAssemblyName = entryModule.Definition.Assembly?.Name.Name ?? Path.GetFileNameWithoutExtension(entryModule.File);
                inspection.EntryType = entryType.FullName;
                inspection.EntryImageFile = entryModule.File;
                ReadAssemblyIdentity(entryModule.Definition, inspection);
                try
                {
                    using Stream s = PackageManifests.FindEntry(archive, entryModule.File)!.Open();
                    inspection.EntryImageHash = SHA256.HashData(s);
                }
                catch (Exception) { /* the [Guid] or the name still identify it */ }

                if (inspection.Runtime == InspectedRuntime.Silverlight)
                {
                    inspection.StartPage = FindStartPage(modules.Select(m => m.Definition), entryModule.Definition, t => DerivesFrom(t, PhonePage));
                    // A managed shell over native components: list them, so the installer's
                    // Direct3D/XAML check (NativeXamlComponent) can find a drawing one.
                    inspection.NativeComponents.AddRange(nativeDlls);
                }
            }
            else
            {
                // No managed entry point. A WP8.1 AppxManifest names its executable; otherwise
                // the package's ARM executable, if it has exactly one (or one named after it).
                string? appxExe = AppxIdentity.Read(archive)?.Executable;
                (string File, byte[] Hash)? exe = nativeExes.FirstOrDefault(e => appxExe != null && e.File.Equals(appxExe, StringComparison.OrdinalIgnoreCase));
                if (exe?.File == null && nativeExes.Count >= 1) exe = nativeExes[0];
                if (exe?.File != null)
                {
                    inspection.Runtime = InspectedRuntime.Native;
                    inspection.RuntimeEvidence = $"{exe.Value.File} is an ARM executable" + (nativeExes.Count > 1 ? $" (first of {nativeExes.Count})" : "");
                    inspection.EntryImageFile = exe.Value.File;
                    inspection.EntryImageHash = exe.Value.Hash;
                }
            }

            inspection.IconPath = IconCandidates.Select(c => PackageManifests.FindEntry(archive, c)?.FullName).FirstOrDefault(c => c != null)
                                  ?? GuessIcon(archive);

            foreach (Module m in modules) m.Definition.Dispose();
            return inspection;
        }

        /// <summary>A shallow PNG whose name says icon or tile (AppIcon.png, AngryBirds_Tile_173x173.png,
        /// assets\icons\ApplicationIcon.png), preferring an icon to a tile and a shallower path.</summary>
        internal static string? GuessIcon(ZipArchive archive)
        {
            static int Rank(string name)
            {
                string file = Path.GetFileNameWithoutExtension(name.Replace('\\', '/').Split('/').Last()).ToLowerInvariant();
                if (file.Contains("applicationicon")) return 0;
                if (file.Contains("icon")) return 1;
                if (file.Contains("tile")) return 2;
                return 9;
            }

            return archive.Entries
                .Where(e => e.FullName.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                .Select(e => (e.FullName, Depth: e.FullName.Count(c => c == '/' || c == '\\'), Rank: Rank(e.FullName)))
                .Where(e => e.Depth <= 2 && e.Rank < 9)
                .OrderBy(e => e.Rank).ThenBy(e => e.Depth).ThenBy(e => e.FullName.Length).ThenBy(e => e.FullName, StringComparer.OrdinalIgnoreCase)
                .Select(e => e.FullName)
                .FirstOrDefault();
        }

        private static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition module)
        {
            foreach (TypeDefinition t in module.Types)
            {
                yield return t;
                foreach (TypeDefinition n in Nested(t)) yield return n;
            }
        }

        private static IEnumerable<TypeDefinition> Nested(TypeDefinition t)
        {
            foreach (TypeDefinition n in t.NestedTypes)
            {
                yield return n;
                foreach (TypeDefinition d in Nested(n)) yield return d;
            }
        }

        private static bool IsFrameworkAssembly(string? name)
            => name == null
               || name.StartsWith("Microsoft.", StringComparison.OrdinalIgnoreCase)
               || name.StartsWith("System.", StringComparison.OrdinalIgnoreCase)
               || name.Equals("mscorlib", StringComparison.OrdinalIgnoreCase);

        private static void ReadAssemblyIdentity(ModuleDefinition module, PackageInspection inspection)
        {
            AssemblyDefinition? asm = module.Assembly;
            if (asm == null) return;
            string? title = null, product = null, fileVersion = null;
            foreach (CustomAttribute attr in asm.CustomAttributes)
            {
                string? value;
                try
                {
                    if (attr.ConstructorArguments.Count != 1) continue;
                    value = attr.ConstructorArguments[0].Value as string;
                }
                catch (Exception)
                {
                    continue; // a blob that needs resolving; not one of ours
                }
                if (string.IsNullOrWhiteSpace(value)) continue;
                switch (attr.AttributeType.FullName)
                {
                    case "System.Reflection.AssemblyTitleAttribute": title = value; break;
                    case "System.Reflection.AssemblyProductAttribute": product = value; break;
                    case "System.Reflection.AssemblyCompanyAttribute": inspection.Company = value; break;
                    case "System.Reflection.AssemblyDescriptionAttribute": inspection.Description = value; break;
                    case "System.Reflection.AssemblyFileVersionAttribute": fileVersion = value; break;
                    case "System.Runtime.InteropServices.GuidAttribute":
                        if (Guid.TryParse(value, out Guid g) && g != Guid.Empty) inspection.AssemblyGuid = g.ToString();
                        break;
                }
            }
            inspection.Title = title ?? product;
            string assemblyVersion = asm.Name.Version?.ToString() ?? "";
            inspection.Version = IsRealVersion(fileVersion) ? fileVersion
                : IsRealVersion(assemblyVersion) ? assemblyVersion
                : null;
        }

        private static bool IsRealVersion(string? v)
            => !string.IsNullOrWhiteSpace(v) && v != "0.0.0.0" && System.Version.TryParse(v, out _);

        /// <summary>
        /// The start page: the XAML a <c>PhoneApplicationPage</c> loads in its
        /// <c>InitializeComponent</c> (<c>"/Asm;component/MainPage.xaml"</c>), preferring a page
        /// called MainPage, then one in the entry assembly.
        /// </summary>
        internal static string? FindStartPage(IEnumerable<ModuleDefinition> modules, ModuleDefinition entry, Func<TypeDefinition, bool> isPage)
        {
            List<(TypeDefinition Type, string Xaml, bool InEntry)> pages = new();
            foreach (ModuleDefinition module in modules)
            {
                foreach (TypeDefinition type in AllTypes(module))
                {
                    if (!type.IsClass || type.IsAbstract || !isPage(type)) continue;
                    string? xaml = ComponentXaml(type);
                    if (xaml != null) pages.Add((type, xaml, module == entry));
                }
            }

            // The VS template's MainPage first; then a page that exists to come first (a splash or
            // loading screen - Flowerz starts at Pages/Loading.xaml, not its MainMenu); then a main
            // or start page.
            static int Rank(string name) =>
                name.Equals("MainPage", StringComparison.OrdinalIgnoreCase) ? 0
                : name.Contains("Splash", StringComparison.OrdinalIgnoreCase) || name.Contains("Loading", StringComparison.OrdinalIgnoreCase) ? 1
                : name.Contains("Main", StringComparison.OrdinalIgnoreCase) || name.Contains("Start", StringComparison.OrdinalIgnoreCase) ? 2
                : 3;

            var best = pages
                .OrderBy(p => p.InEntry ? 0 : 1)
                .ThenBy(p => Rank(p.Type.Name))
                .ThenBy(p => p.Type.FullName, StringComparer.Ordinal)
                .FirstOrDefault();
            if (best.Type == null) return null;
            return best.InEntry ? best.Xaml : "/" + best.Type.Module.Assembly.Name.Name + ";component/" + best.Xaml;
        }

        private static string? ComponentXaml(TypeDefinition type)
        {
            MethodDefinition? init = type.Methods.FirstOrDefault(m => m.Name == "InitializeComponent" && m.HasBody);
            if (init == null) return null;
            try
            {
                foreach (Instruction i in init.Body.Instructions)
                {
                    if (i.OpCode.Code == Code.Ldstr && i.Operand is string s)
                    {
                        int at = s.IndexOf(";component/", StringComparison.OrdinalIgnoreCase);
                        if (at >= 0 && s.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase))
                        {
                            return s.Substring(at + ";component/".Length);
                        }
                    }
                }
            }
            catch (Exception)
            {
                // An unreadable body: not a page we can name.
            }
            return null;
        }

        internal enum PeKind { NotPe, Managed, NativeArm, NativeOther }

        internal static PeKind ClassifyPe(byte[] b)
        {
            if (b.Length < 0x40 || b[0] != 'M' || b[1] != 'Z') return PeKind.NotPe;
            int pe = BitConverter.ToInt32(b, 0x3C);
            if (pe <= 0 || pe + 24 > b.Length || b[pe] != 'P' || b[pe + 1] != 'E' || b[pe + 2] != 0 || b[pe + 3] != 0) return PeKind.NotPe;
            ushort machine = BitConverter.ToUInt16(b, pe + 4);
            int optional = pe + 24;
            if (optional + 2 > b.Length) return PeKind.NotPe;
            ushort magic = BitConverter.ToUInt16(b, optional);
            int dataDirectories = magic == 0x20B ? optional + 112 : optional + 96;
            int clrEntry = dataDirectories + 14 * 8; // IMAGE_DIRECTORY_ENTRY_COM_DESCRIPTOR
            bool hasClr = clrEntry + 8 <= b.Length && BitConverter.ToUInt32(b, clrEntry) != 0;
            if (hasClr) return PeKind.Managed;
            return machine == 0x01C4 ? PeKind.NativeArm : PeKind.NativeOther;
        }

        private sealed class NullResolver : IAssemblyResolver
        {
            public static readonly NullResolver Instance = new();
            public AssemblyDefinition? Resolve(AssemblyNameReference name) => null;
            public AssemblyDefinition? Resolve(AssemblyNameReference name, ReaderParameters parameters) => null;
            public void Dispose() { }
        }
    }

    /// <summary>The identity a WP8.1 <c>AppxManifest.xml</c> declares, by local name (the schema
    /// namespaces vary between 8.1 Silverlight and WinRT packages).</summary>
    internal sealed class AppxIdentity
    {
        public string? ProductId;
        public string? DisplayName;
        public string? Publisher;
        public string? Version;
        public string? Executable;
        public string? Logo;

        public static AppxIdentity? Read(ZipArchive archive)
        {
            ZipArchiveEntry? entry = PackageManifests.FindEntry(archive, PackageManifests.AppxManifestName);
            if (entry == null) return null;
            try
            {
                XmlDocument doc = new XmlDocument();
                using (Stream s = entry.Open()) doc.Load(s);
                XmlElement? Find(string localName) =>
                    doc.SelectSingleNode($"//*[local-name()='{localName}']") as XmlElement;

                XmlElement? identity = Find("Identity");
                XmlElement? application = Find("Application");
                string? Text(string localName) => Find(localName)?.InnerText?.Trim();
                string? NonEmpty(string? v) => string.IsNullOrWhiteSpace(v) ? null : v;

                return new AppxIdentity
                {
                    ProductId = NonEmpty(Find("PhoneIdentity")?.GetAttribute("PhoneProductId")),
                    DisplayName = NonEmpty(Text("DisplayName")),
                    Publisher = NonEmpty(Text("PublisherDisplayName")),
                    Version = NonEmpty(identity?.GetAttribute("Version")),
                    Executable = NonEmpty(application?.GetAttribute("Executable")),
                    Logo = NonEmpty(Text("Logo"))?.Replace('\\', '/'),
                };
            }
            catch (Exception ex)
            {
                Log.Warn(LogCategory.AppInstall, $"AppxManifest.xml unreadable: {ex.Message}");
                return null;
            }
        }
    }
}
