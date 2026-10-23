using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Deployment</c>. Singleton describing the
    /// installed XAP. <c>Current</c> is exposed so user code can access entry-point
    /// info, and <see cref="Parts"/> lists the manifest's assemblies. Derives from <see cref="DependencyObject"/> to match
    /// Silverlight — games cast <c>Deployment.Current</c> to <c>DependencyObject</c> to
    /// reach <c>.Dispatcher</c> (e.g. Beards &amp; Beaks' PerformActionOnUIThread).
    ///
    /// <para>On the phone these values are the package's <c>AppManifest.xaml</c>, for XNA
    /// titles as much as Silverlight ones. <see cref="LoadForLaunch"/> reads them from the
    /// install folder. Libraries rely on it: Microsoft's ad SDK (<c>AdManager</c>'s type
    /// initializer) does <c>Assembly.Load(Deployment.Current.EntryPointAssembly)</c>, so a
    /// null here makes every ad call in the process throw <c>TypeInitializationException</c>
    /// (AE Fruit Slash).</para></summary>
    public class Deployment : DependencyObject
    {
        public static Deployment Current { get; } = new Deployment();
        public AssemblyPartCollection Parts { get; } = new AssemblyPartCollection();
        public string? EntryPointAssembly { get; set; }
        public string? EntryPointType { get; set; }
        public string? RuntimeVersion { get; set; }

        /// <summary>WPR-only: fill <see cref="Current"/> from <c>AppManifest.xaml</c> in
        /// <paramref name="installFolder"/>. A missing or unreadable manifest leaves the values
        /// null, which is what this shim answered before.</summary>
        public static void LoadForLaunch(string? installFolder)
        {
            ResetForNewLaunch();
            if (string.IsNullOrEmpty(installFolder))
            {
                return;
            }

            string path = System.IO.Path.Combine(installFolder, "AppManifest.xaml");
            if (!File.Exists(path))
            {
                return;
            }

            try
            {
                XElement root = XDocument.Load(path).Root!;
                Current.EntryPointAssembly = (string?)root.Attribute("EntryPointAssembly");
                Current.EntryPointType = (string?)root.Attribute("EntryPointType");
                Current.RuntimeVersion = (string?)root.Attribute("RuntimeVersion");
                foreach (XElement part in root.Descendants())
                {
                    if (part.Name.LocalName == "AssemblyPart"
                        && (string?)part.Attribute("Source") is { Length: > 0 } source)
                    {
                        Current.Parts.Add(new AssemblyPart { Source = source });
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Trace.WriteLine($"[wpr-deployment] could not read {path}: {ex.Message}");
            }
        }

        /// <summary>WPR-only: forget the previous launch's manifest.</summary>
        public static void ResetForNewLaunch()
        {
            Current.EntryPointAssembly = null;
            Current.EntryPointType = null;
            Current.RuntimeVersion = null;
            Current.Parts.Clear();
        }
    }
}
