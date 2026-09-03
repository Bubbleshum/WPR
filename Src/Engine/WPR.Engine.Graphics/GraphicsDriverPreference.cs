#nullable enable
using System;
using System.IO;

namespace WPR.Engine.Graphics
{
    /// <summary>
    /// The platform's declared graphics-driver choice, and the runtime override that can beat it.
    ///
    /// <para><b>What moved here and what deliberately did not.</b> This decision used to be split
    /// across two assemblies: the Android head's <c>GraphicsDriverPolicy</c> decided *which* driver
    /// (emulator detection, override file, logging) and
    /// <c>WPR.Backend.FNA.GraphicsDriverSelection</c> set the SDL hint. The half that is
    /// platform-independent — reading an override file, turning a choice into a driver name — is
    /// here. The two halves that are not stayed where they belong: <b>emulator detection</b> is in
    /// the Android head, because it reads <c>Android.OS.Build</c> and only a head can; and
    /// <b>applying the hint</b> is in the FNA backend, because only it knows SDL. A head therefore
    /// declares an answer, not a policy.
    /// </para>
    ///
    /// <para><b>Keep the failure direction.</b> Emulator detection on the head side is biased to
    /// false negatives on purpose: a missed emulator only means the emulator renders nothing,
    /// whereas a false positive puts a real phone back on the Vulkan driver that T-poses every
    /// <c>SkinnedEffect</c> character. Never invert it into "force OpenGL only when we detect
    /// hardware".</para>
    /// </summary>
    public static class GraphicsDriverPreference
    {
        /// <summary>
        /// Drop a file with this name in the declared override directory containing
        /// <c>OpenGL</c>, <c>Vulkan</c>, <c>D3D11</c> or <c>auto</c> to change the driver without a
        /// rebuild. It exists for triaging a device whose GL driver misbehaves — the alternative is
        /// asking someone to wait for a new build to test a one-word change.
        /// </summary>
        public const string OverrideFileName = "fna3d_driver.txt";

        private static readonly object _gate = new object();
        private static GraphicsDriver _declared = GraphicsDriver.Unspecified;
        private static string? _overrideDirectory;

        /// <summary>What the platform asked for, before any override file is considered.</summary>
        public static GraphicsDriver Declared
        {
            get { lock (_gate) { return _declared; } }
        }

        /// <summary>
        /// False when no platform declared anything — in which case the caller must leave the
        /// driver lever completely alone rather than clearing it. See
        /// <see cref="GraphicsDriver.Unspecified"/>.
        /// </summary>
        public static bool HasPreference => Declared != GraphicsDriver.Unspecified;

        /// <summary>Records the platform's choice. Called by the composition root.</summary>
        /// <param name="driver">The declared driver.</param>
        /// <param name="overrideDirectory">Where to look for <see cref="OverrideFileName"/>, or
        /// null to disable the override entirely.</param>
        public static void Declare(GraphicsDriver driver, string? overrideDirectory)
        {
            lock (_gate)
            {
                _declared = driver;
                _overrideDirectory = overrideDirectory;
            }
        }

        /// <summary>
        /// The driver name to force, or <b>null for automatic</b> (clear the force).
        ///
        /// <para>Only meaningful when <see cref="HasPreference"/> is true — null here means
        /// "automatic", which is a different instruction from "do not touch it", and a caller must
        /// not conflate the two.</para>
        ///
        /// <para>An override file wins over the declaration, including when it says <c>auto</c>.
        /// An unreadable or empty one is ignored rather than fatal: a triage aid must never stop a
        /// game launching.</para>
        /// </summary>
        public static string? ResolveDriverName()
        {
            GraphicsDriver declared;
            string? directory;
            lock (_gate)
            {
                declared = _declared;
                directory = _overrideDirectory;
            }

            string? overridden = ReadOverride(directory);
            if (overridden != null)
            {
                return IsAutomatic(overridden) ? null : overridden;
            }

            switch (declared)
            {
                case GraphicsDriver.OpenGL: return "OpenGL";
                case GraphicsDriver.Vulkan: return "Vulkan";
                case GraphicsDriver.D3D11: return "D3D11";
                default: return null;
            }
        }

        /// <summary>A one-line description of the resolved choice, for the launch log.</summary>
        public static string Describe()
        {
            if (!HasPreference)
            {
                return "driver=(platform default, lever untouched)";
            }
            string? name = ResolveDriverName();
            return "driver=" + (name ?? "automatic");
        }

        private static bool IsAutomatic(string value) =>
            value.Length == 0 || string.Equals(value, "auto", StringComparison.OrdinalIgnoreCase);

        private static string? ReadOverride(string? directory)
        {
            if (string.IsNullOrEmpty(directory))
            {
                return null;
            }

            try
            {
                string path = Path.Combine(directory!, OverrideFileName);
                if (!File.Exists(path))
                {
                    return null;
                }

                string content = File.ReadAllText(path).Trim();
                return content.Length == 0 ? null : content;
            }
            catch (Exception)
            {
                /* Unreadable override: ignore it. Deliberately silent — this project has no logger
                 * dependency, and the composition root already reports the resolved choice, which
                 * is the fact that matters. */
                return null;
            }
        }
    }
}
