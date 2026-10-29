using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace WPR.Engine.Online
{
    /// <summary>
    /// A package's version as its own manifest states it, read the way WPR Hub reads it from the
    /// packages it stores (<c>App\Support\PackageManifest::read</c>): <c>WMAppManifest.xml</c>'s
    /// first <c>App</c> element's <c>Version</c>, else <c>AppxManifest.xml</c>'s first
    /// <c>Identity</c>, else an APPX bundle's <c>AppxMetadata/AppxBundleManifest.xml</c>; digits
    /// and dots only, at most four parts.
    /// </summary>
    /// <remarks>
    /// <para>The hub compares this string with the versions of packages it holds to decide that it
    /// needs no more of a game (<c>not_needed</c>), so it must be the manifest's own text, not
    /// <c>Application.Version</c>: WPR resolves that one further (a placeholder <c>1.0.0.0</c>
    /// becomes the entry assembly's version), and a resolved version could name a build the hub
    /// does not have.</para>
    /// </remarks>
    public static class PackageManifestVersion
    {
        private static readonly string[] Manifests =
            { "WMAppManifest.xml", "AppxManifest.xml", "AppxMetadata/AppxBundleManifest.xml" };

        // [0-9], not \d: .NET's \d also matches other scripts' digits, PHP's (no /u) does not.
        private static readonly Regex Shape = new Regex(@"^[0-9]+(\.[0-9]+){0,3}$", RegexOptions.CultureInvariant);

        /// <summary>From the package itself (a XAP or APPX ZIP). Null when it is not one or states none.</summary>
        public static string? FromPackage(Stream package)
        {
            if (!package.CanSeek || !package.CanRead) return null;
            long position = package.Position;
            try
            {
                package.Position = 0;
                using ZipArchive zip = new ZipArchive(package, ZipArchiveMode.Read, leaveOpen: true);
                foreach (string name in Manifests)
                {
                    // At the package root, any case, as the hub's ZipArchive::locateName(FL_NOCASE).
                    ZipArchiveEntry? entry = zip.Entries.FirstOrDefault(e =>
                        string.Equals(e.FullName.Replace('\\', '/'), name, StringComparison.OrdinalIgnoreCase));
                    if (entry is null) continue;
                    // The hub stops at the first manifest it finds, whatever it holds.
                    if (entry.Length > 1024 * 1024) return null;
                    using Stream s = entry.Open();
                    return Read(s, name);
                }
                return null;
            }
            catch (Exception)
            {
                return null; // not a ZIP (a DRM-wrapped download), or a damaged one
            }
            finally
            {
                try { package.Position = position; } catch (Exception) { /* closed under us */ }
            }
        }

        /// <summary>
        /// From an install folder, for installs whose <c>wpr-package.json</c> predates the version.
        /// The extracted manifests are the package's own; where WPR completed a broken one it kept
        /// the package's as <c>.original</c>, which is what the hub reads, so that is read here.
        /// </summary>
        public static string? FromInstallFolder(string? installFolder)
        {
            if (string.IsNullOrEmpty(installFolder)) return null;
            try
            {
                foreach (string name in Manifests)
                {
                    string path = Path.Combine(installFolder, name.Replace('/', Path.DirectorySeparatorChar));
                    string original = path + ".original";
                    string? file = File.Exists(original) ? original : File.Exists(path) ? path : null;
                    if (file is null) continue;
                    using FileStream s = File.OpenRead(file);
                    return Read(s, name);
                }
            }
            catch (Exception) { /* unreadable: no version */ }
            return null;
        }

        /// <summary>The <c>Version</c> of the manifest's identifying element, when it has the hub's shape.</summary>
        internal static string? Read(Stream manifest, string name)
        {
            try
            {
                using XmlReader reader = XmlReader.Create(manifest, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
                XDocument doc = XDocument.Load(reader);
                string element = name.StartsWith("WMAppManifest", StringComparison.OrdinalIgnoreCase) ? "App" : "Identity";
                XElement? identity = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == element);
                return Normalise(identity?.Attributes().FirstOrDefault(a => a.Name.LocalName == "Version")?.Value);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Trimmed; null unless digits and dots, one to four parts.</summary>
        public static string? Normalise(string? version)
        {
            string s = (version ?? "").Trim();
            return Shape.IsMatch(s) ? s : null;
        }
    }
}
