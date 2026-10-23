using System.Reflection;

namespace WPR.WindowsCompability
{
    /// <summary>
    /// Answers <see cref="Module.Name"/> as the phone did for a game's own assemblies.
    ///
    /// <para>WPR loads game assemblies with <c>AssemblyLoadContext.LoadFromStream</c> so the
    /// files are not locked (see <c>ApplicationLaunch.LoadAssemblyWithoutFileLock</c>). A module
    /// loaded that way has no file, and .NET reports its <see cref="Module.Name"/> as
    /// <c>"&lt;Unknown&gt;"</c>. On the phone it was the file name, <c>CutFruit.dll</c>, and code
    /// finds its own assembly by it. AEMobile's framework reads its version from the assembly
    /// whose <c>ManifestModule.Name</c> matches <c>EntryPointAssembly + ".dll"</c>; with no match
    /// the version is null, its <c>StateManager</c> throws on <c>Path.Combine(.., null)</c> and
    /// no setting or score is ever saved (AE Fruit Slash).</para>
    ///
    /// <para>Every <c>Module::get_Name</c> call site in a patched game is rewritten to
    /// <see cref="GetName"/> by <c>ApplicationPatcher.RedirectModuleNameCalls</c>. A module with
    /// a file keeps its real name; one without falls back to <see cref="Module.ScopeName"/>, the
    /// name recorded in the module's own metadata, which compilers set to the file name.</para>
    /// </summary>
    public static class ReflectionModules
    {
        /// <summary>The name .NET gives a module that was not loaded from a file.</summary>
        public const string UnknownName = "<Unknown>";

        public static string GetName(Module module)
        {
            string name = module.Name;
            return name == UnknownName ? module.ScopeName : name;
        }
    }
}
