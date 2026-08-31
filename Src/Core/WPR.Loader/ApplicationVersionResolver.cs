using System;
using System.IO;
using System.IO.Compression;
using System.Xml;
using Mono.Cecil;
using WPR.Common;

namespace WPR
{
    /// <summary>
    /// Works out the version to show for a XAP.
    ///
    /// <para><b>Why this is not just <c>WMAppManifest.xml</c>'s <c>App@Version</c>.</b> That
    /// attribute is the deployment version, and a large share of shipped WP7 titles never
    /// bumped it off the project-template default of <c>1.0.0.0</c> — measured across a
    /// 307-title library, <b>153 of 295</b> report exactly that, including builds whose real
    /// version is years later. Mirror's Edge is the worked example: its manifest says
    /// <c>1.0.0.0</c> while the entry assembly says <c>1.1.31.0</c>.</para>
    ///
    /// <para><b>The rule: manifest first, and only fall back when it is the placeholder.</b>
    /// The manifest version is the one the Marketplace showed and is what a user means by "the
    /// version", so it wins whenever it carries information. When it does not, the entry
    /// assembly is consulted — <c>AssemblyFileVersion</c> before <c>AssemblyVersion</c>,
    /// because by convention the former is the product version and the latter is a binding
    /// identity that build systems sometimes auto-stamp.</para>
    ///
    /// <para><b>Measured effect</b> over that same library, scoring each candidate against the
    /// version in the XAP's own filename (the closest available ground truth):</para>
    /// <list type="table">
    ///   <item><description>manifest alone — 195 agree, 153 report <c>1.0.0.0</c></description></item>
    ///   <item><description>this rule — 207 agree, 114 report <c>1.0.0.0</c></description></item>
    /// </list>
    ///
    /// <para>It is an improvement, not a cure. Roughly 63 titles have no better source anywhere
    /// in the package — manifest and both assembly attributes are all the placeholder — and a
    /// handful of assemblies carry an internal build stamp (<c>1.0.3947.33471</c>) rather than
    /// a marketing version. Nothing in a XAP records the Marketplace version except the manifest
    /// field the developer left alone, so those cannot be recovered from the file at all.
    /// <c>AssemblyInformationalVersion</c> is not consulted: it is present in zero of the 307.</para>
    /// </summary>
    public static class ApplicationVersionResolver
    {
        /// <summary>
        /// Versions that carry no information. <c>1.0.0.0</c> is the Visual Studio project
        /// template default; <c>0.0.0.0</c> is what an assembly reports with no version
        /// attribute at all.
        /// </summary>
        private static bool IsPlaceholder(string? version)
            => string.IsNullOrWhiteSpace(version)
               || version == "1.0.0.0"
               || version == "0.0.0.0";

        /// <summary>
        /// Resolves the display version for a package.
        /// </summary>
        /// <param name="archive">The opened XAP. Not disposed, and left positioned as found.</param>
        /// <param name="manifestVersion"><c>App@Version</c> from <c>WMAppManifest.xml</c>.</param>
        /// <param name="titleForLog">Title used only in the diagnostic line.</param>
        /// <returns>
        /// The best available version. Never null; falls back to <paramref name="manifestVersion"/>
        /// (placeholder and all) when the package offers nothing better, so the caller always has
        /// the same value it would have had before.
        /// </returns>
        public static string Resolve(ZipArchive archive, string? manifestVersion, string? titleForLog = null)
        {
            string manifest = manifestVersion ?? string.Empty;

            if (!IsPlaceholder(manifest))
            {
                return manifest;
            }

            try
            {
                string? entryAssemblyPath = FindEntryAssemblyPath(archive);
                if (entryAssemblyPath == null)
                {
                    return manifest;
                }

                ZipArchiveEntry? dll = archive.GetEntry(entryAssemblyPath);
                if (dll == null)
                {
                    return manifest;
                }

                (string? fileVersion, string? assemblyVersion) = ReadAssemblyVersions(dll);

                // AssemblyFileVersion first: by convention it is the product version, while
                // AssemblyVersion is the binding identity a build system may auto-stamp.
                string? better = !IsPlaceholder(fileVersion) ? fileVersion
                               : !IsPlaceholder(assemblyVersion) ? assemblyVersion
                               : null;

                if (better == null)
                {
                    return manifest;
                }

                Log.Info(LogCategory.AppInstall,
                    $"Version for '{titleForLog ?? entryAssemblyPath}': manifest said " +
                    $"'{(string.IsNullOrEmpty(manifest) ? "(none)" : manifest)}', using '{better}' from " +
                    $"{(!IsPlaceholder(fileVersion) ? "AssemblyFileVersion" : "AssemblyVersion")} of {entryAssemblyPath}.");

                return better;
            }
            catch (Exception ex)
            {
                // A version is cosmetic — never let reading one fail a scan or an install.
                Log.Warn(LogCategory.AppInstall,
                    $"Could not read a version from the entry assembly of '{titleForLog ?? "(unknown)"}': " +
                    $"{ex.GetType().Name}: {ex.Message}. Falling back to the manifest value.");
                return manifest;
            }
        }

        /// <summary>
        /// Same rule, applied to an already-extracted install folder rather than a XAP. Lets
        /// <see cref="ApplicationInstaller.RepatchAsync"/> correct a version that was recorded
        /// before this resolver existed, without making the user re-extract tens of gigabytes.
        ///
        /// <para>Reading the <em>patched</em> DLL is fine: <see cref="ApplicationPatcher"/>
        /// rewrites assembly references, typeref scopes and the assembly's simple name, but
        /// never its <c>Name.Version</c> or its custom attributes, so both version sources
        /// survive patching unchanged.</para>
        /// </summary>
        /// <param name="installFolder">The per-product folder under <c>AppData</c>.</param>
        /// <param name="entryAssemblyFile">
        /// <c>Application.Assembly</c> — the entry assembly's file name as recorded at install.
        /// </param>
        /// <param name="manifestVersion">The version currently recorded for the app.</param>
        public static string ResolveFromInstallFolder(
            string installFolder,
            string? entryAssemblyFile,
            string? manifestVersion,
            string? titleForLog = null)
        {
            string current = manifestVersion ?? string.Empty;

            if (!IsPlaceholder(current) || string.IsNullOrWhiteSpace(entryAssemblyFile))
            {
                return current;
            }

            try
            {
                string dllPath = Path.Combine(installFolder, entryAssemblyFile!.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(dllPath))
                {
                    return current;
                }

                (string? fileVersion, string? assemblyVersion) = ReadAssemblyVersions(File.ReadAllBytes(dllPath));

                string? better = !IsPlaceholder(fileVersion) ? fileVersion
                               : !IsPlaceholder(assemblyVersion) ? assemblyVersion
                               : null;

                if (better == null)
                {
                    return current;
                }

                Log.Info(LogCategory.AppInstall,
                    $"Version for '{titleForLog ?? entryAssemblyFile}': recorded as " +
                    $"'{(string.IsNullOrEmpty(current) ? "(none)" : current)}', corrected to '{better}' " +
                    $"from the installed entry assembly.");

                return better;
            }
            catch (Exception ex)
            {
                Log.Warn(LogCategory.AppInstall,
                    $"Could not re-read the version for '{titleForLog ?? "(unknown)"}': " +
                    $"{ex.GetType().Name}: {ex.Message}. Leaving it as recorded.");
                return current;
            }
        }

        /// <summary>
        /// Resolves the entry assembly's path inside the package from <c>AppManifest.xaml</c>,
        /// the same way <see cref="ApplicationInstaller"/> does when it records
        /// <c>Application.Assembly</c>: the <c>AssemblyPart</c> whose name matches
        /// <c>EntryPointAssembly</c> supplies the <c>Source</c>. Falls back to
        /// <c>&lt;EntryPointAssembly&gt;.dll</c>, which is what all but a handful use.
        /// </summary>
        private static string? FindEntryAssemblyPath(ZipArchive archive)
        {
            ZipArchiveEntry? manifestEntry = archive.GetEntry("AppManifest.xaml");
            if (manifestEntry == null)
            {
                // WP8 "Modern Native" packages have no Silverlight manifest. They are rejected
                // at install anyway; here it just means there is no managed assembly to ask.
                return null;
            }

            XmlDocument doc = new XmlDocument();
            using (Stream stream = manifestEntry.Open())
            {
                doc.Load(stream);
            }

            XmlNode? deployment = doc.DocumentElement;
            string? entryName = deployment?.Attributes?["EntryPointAssembly"]?.Value;
            if (string.IsNullOrWhiteSpace(entryName))
            {
                return null;
            }

            XmlNamespaceManager ns = new XmlNamespaceManager(doc.NameTable);
            ns.AddNamespace("a", "http://schemas.microsoft.com/client/2007/deployment");
            XmlNodeList? parts = deployment!.SelectNodes("//a:Deployment.Parts//a:AssemblyPart", ns);

            if (parts != null)
            {
                foreach (XmlNode? part in parts)
                {
                    XmlAttribute? name = part!.Attributes!["x:Name"] ?? part.Attributes!["Name"];
                    if (name?.Value != entryName)
                    {
                        continue;
                    }

                    string? source = part.Attributes!["Source"]?.Value;
                    if (!string.IsNullOrWhiteSpace(source))
                    {
                        return source!.Replace('\\', '/');
                    }
                }
            }

            return entryName + ".dll";
        }

        /// <summary>
        /// Reads the two version attributes out of assembly metadata.
        ///
        /// <para>The entry has to be copied to a <see cref="MemoryStream"/> first: a zip entry
        /// opens as a forward-only deflate stream and Cecil needs to seek. Reading is
        /// <see cref="ReadingMode.Immediate"/> so nothing is left pointing at a stream this
        /// method is about to dispose.</para>
        /// </summary>
        private static (string? FileVersion, string? AssemblyVersion) ReadAssemblyVersions(ZipArchiveEntry dll)
        {
            using MemoryStream buffer = new MemoryStream();
            using (Stream source = dll.Open())
            {
                source.CopyTo(buffer);
            }
            return ReadAssemblyVersions(buffer.ToArray());
        }

        /// <summary>
        /// Reads from bytes rather than a path so the file is never left open — an installed
        /// DLL is about to be rewritten in place by the patcher on the repatch path.
        /// </summary>
        private static (string? FileVersion, string? AssemblyVersion) ReadAssemblyVersions(byte[] image)
        {
            using MemoryStream buffer = new MemoryStream(image, writable: false);

            using AssemblyDefinition assembly = AssemblyDefinition.ReadAssembly(
                buffer,
                new ReaderParameters
                {
                    ReadingMode = ReadingMode.Immediate,
                    // Never go looking for referenced assemblies — see below.
                    AssemblyResolver = NonResolvingAssemblyResolver.Instance,
                });

            /* Read this FIRST and unconditionally. The assembly's own version is a plain
             * metadata field: no resolution, no blob decoding, nothing that can fail. */
            string? assemblyVersion = assembly.Name.Version?.ToString();

            /* AssemblyFileVersion is a CUSTOM ATTRIBUTE, and that is a different matter.
             * Decoding an attribute blob makes Cecil resolve the attribute's declaring
             * assembly, because it needs the constructor signature to know how to read the
             * bytes. For a WP7 assembly that is Silverlight's mscorlib / System 2.0.5.0,
             * which exists nowhere on an Android device.
             *
             * On 2026-08-31 this threw AssemblyResolutionException on device and the outer
             * handler swallowed it, discarding the perfectly good AssemblyVersion that had
             * already been read — so every Android repatch left the version at 1.0.0.0 while
             * the same code worked on desktop, where the Cecil staging folder happens to hold
             * those reference assemblies. Hence both the non-resolving resolver above and
             * this local catch: the file version is a nice-to-have, the assembly version is
             * the one that actually carries the answer for most titles. */
            string? fileVersion = null;
            try
            {
                foreach (CustomAttribute attribute in assembly.CustomAttributes)
                {
                    if (attribute.AttributeType.Name != "AssemblyFileVersionAttribute") continue;
                    if (attribute.ConstructorArguments.Count != 1) continue;
                    fileVersion = Normalize(attribute.ConstructorArguments[0].Value as string);
                    break;
                }
            }
            catch (Exception ex)
            {
                Log.Info(LogCategory.AppInstall,
                    $"AssemblyFileVersion unreadable ({ex.GetType().Name}); using AssemblyVersion.");
            }

            return (fileVersion, assemblyVersion);
        }

        /// <summary>
        /// Hands Cecil a resolver that always answers "not found" instead of throwing.
        ///
        /// <para>Reading a version must never depend on a game's references being present:
        /// they are Silverlight/WP7 assemblies that do not exist on the running machine, and
        /// Cecil's default resolver throws <c>AssemblyResolutionException</c> rather than
        /// returning null. Returning null lets the metadata read complete with the parts that
        /// do not need resolution — which is the part carrying the version.</para>
        /// </summary>
        private sealed class NonResolvingAssemblyResolver : IAssemblyResolver
        {
            public static readonly NonResolvingAssemblyResolver Instance = new();

            public AssemblyDefinition? Resolve(AssemblyNameReference name) => null;

            public AssemblyDefinition? Resolve(AssemblyNameReference name, ReaderParameters parameters) => null;

            public void Dispose()
            {
            }
        }

        /// <summary>
        /// Trims an <c>AssemblyFileVersion</c> to the numeric part. The attribute is a free
        /// string and titles do put things like <c>"1.2.3 (retail)"</c> in it.
        /// </summary>
        private static string? Normalize(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;

            int end = 0;
            while (end < raw!.Length && (char.IsDigit(raw[end]) || raw[end] == '.')) end++;

            string trimmed = raw.Substring(0, end).Trim('.');
            return trimmed.Length == 0 ? null : trimmed;
        }
    }
}
