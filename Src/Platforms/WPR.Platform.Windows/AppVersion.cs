using System.Reflection;

namespace WPR.Platform.Windows
{
    /// <summary>
    /// The product version, read once from the assembly rather than typed into XAML.
    ///
    /// <para>It used to be hardcoded in two places — the main window title and the About page —
    /// and they had already drifted apart (About said 0.0.20 while the title still said 0.0.18).
    /// Both now call <see cref="Display"/>, so the only place a version is written is
    /// <c>$(WprVersion)</c> in <c>Src/Directory.Build.props</c>, which flows here through
    /// <c>InformationalVersion</c> and is overridden by the release workflow per release.</para>
    /// </summary>
    public static class AppVersion
    {
        /// <summary>e.g. <c>0.0.20-alpha</c>. Falls back to the plain assembly version if the
        /// informational attribute is missing, and to a placeholder if neither is present.</summary>
        public static string Display { get; } = Resolve();

        /// <summary>e.g. <c>WPR 0.0.20-alpha</c> — the window title / heading form.</summary>
        public static string TitleText => $"WPR {Display}";

        private static string Resolve()
        {
            Assembly asm = typeof(AppVersion).Assembly;

            string? informational = asm
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

            // SourceLink appends "+<commit sha>" to InformationalVersion; trim it for display.
            if (!string.IsNullOrWhiteSpace(informational))
            {
                int plus = informational!.IndexOf('+');
                return plus >= 0 ? informational.Substring(0, plus) : informational;
            }

            return asm.GetName().Version?.ToString(3) ?? "unknown";
        }
    }
}
