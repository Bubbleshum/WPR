using System;
using System.IO;
using Mono.Cecil;


namespace WPR
{
    /// <summary>
    /// Decides whether an installation is a WP7.1 Silverlight/XNA <em>mixed-mode</em> application —
    /// one with no <c>Game</c> subclass, whose loop is a <c>GameTimer</c> on a
    /// <c>PhoneApplicationPage</c> and whose device comes from a
    /// <c>SharedGraphicsDeviceManager</c>.
    /// </summary>
    /// <remarks>
    /// <para><b>Why this is not answered by <c>ApplicationType</c>.</b> The installer classifies
    /// on <c>WMAppManifest.xml</c>'s <c>RuntimeType</c> alone, and a mixed-mode title declares
    /// <c>RuntimeType="Silverlight"</c> — the same value as a genuine Silverlight UI app, which
    /// WPR cannot host. The manifest simply does not distinguish them. (Three titles in the
    /// library declare <c>RuntimeType="XNA"</c> while still referencing the interop assembly;
    /// those have a real <c>Game</c> and take the ordinary XNA path, which is why this is only
    /// consulted on the Silverlight branch.)</para>
    ///
    /// <para><b>Why a type reference and not an assembly reference.</b> Before patching, the
    /// giveaway is a reference to the <c>Microsoft.Xna.Framework.Interop</c> assembly. After
    /// patching there is no such reference — the patcher rescopes those types into
    /// <c>WPR.Framework.Xna</c> — so an assembly-name test would answer "yes" on a fresh install
    /// and "no" after the first repatch. The TYPE name survives both, because rescoping changes
    /// a typeref's scope and not its name.</para>
    /// </remarks>
    public static class MixedModeDetection
    {
        private const string SharedManagerTypeName = "Microsoft.Xna.Framework.SharedGraphicsDeviceManager";

        /// <summary>
        /// True when <paramref name="assemblyFileName"/> in <paramref name="installFolder"/>
        /// names <c>SharedGraphicsDeviceManager</c>.
        /// </summary>
        /// <remarks>
        /// Reads the main assembly only, and only on the Silverlight branch of launch — where
        /// the alternative outcome today is an immediate "not implemented" throw, so one Cecil
        /// open costs nothing anybody can perceive. Any failure answers <c>false</c>: a title we
        /// cannot read is not one we should try to host as a game, and the caller's existing
        /// error is a better report than whatever Cecil threw.
        /// </remarks>
        public static bool IsMixedMode(string installFolder, string? assemblyFileName, Action<string>? trace = null)
        {
            if (string.IsNullOrEmpty(installFolder) || string.IsNullOrEmpty(assemblyFileName)) return false;

            try
            {
                string file = AssemblyNameStandardization.Process(assemblyFileName!);
                string path = Path.Combine(installFolder, file);
                if (!File.Exists(path)) return false;

                using (ModuleDefinition module = ModuleDefinition.ReadModule(path))
                {
                    foreach (TypeReference t in module.GetTypeReferences())
                    {
                        if (string.Equals(t.FullName, SharedManagerTypeName, StringComparison.Ordinal))
                        {
                            trace?.Invoke($"[wpr-mixed] {file} references {SharedManagerTypeName} — " +
                                          "treating as a Silverlight/XNA mixed-mode app.");
                            return true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                trace?.Invoke("[wpr-mixed] mixed-mode probe failed (treating as not mixed-mode): " + ex.Message);
            }

            return false;
        }
    }
}
