namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Shim for <c>System.Windows.AssemblyPart</c>: one <c>AssemblyPart</c> of the package's
    /// <c>AppManifest.xaml</c>, as <see cref="Deployment.Parts"/> lists them. OpenXLive reads
    /// <see cref="Source"/> of each to find the game's assemblies and their resources.
    /// </summary>
    public class AssemblyPart : DependencyObject
    {
        /// <summary>The assembly's file name in the package./summary>
        public string? Source { get; set; }
    }
}
