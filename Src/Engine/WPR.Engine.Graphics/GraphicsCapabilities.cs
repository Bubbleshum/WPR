#nullable enable
using System;
using System.Globalization;

namespace WPR.Engine.Graphics
{
    /// <summary>
    /// A plain immutable snapshot of <see cref="IGraphicsCapabilities"/>. Produced by the backend
    /// once a device exists, and reconstructed from disk by whichever process wants to display it
    /// (see <see cref="GraphicsCapabilitiesStore"/>).
    /// </summary>
    public sealed class GraphicsCapabilities : IGraphicsCapabilities
    {
        public GraphicsCapabilities(
            string? backend,
            bool? supportsOffThreadResourceCreation,
            bool supportsDxt1,
            bool supportsS3tc,
            bool supportsBc7,
            bool supportsHardwareInstancing,
            bool supportsNoOverwrite,
            bool supportsSrgbRenderTargets,
            int maxTextureSlots,
            int maxVertexTextureSlots)
        {
            Backend = backend;
            SupportsOffThreadResourceCreation = supportsOffThreadResourceCreation;
            SupportsDxt1 = supportsDxt1;
            SupportsS3tc = supportsS3tc;
            SupportsBc7 = supportsBc7;
            SupportsHardwareInstancing = supportsHardwareInstancing;
            SupportsNoOverwrite = supportsNoOverwrite;
            SupportsSrgbRenderTargets = supportsSrgbRenderTargets;
            MaxTextureSlots = maxTextureSlots;
            MaxVertexTextureSlots = maxVertexTextureSlots;
        }

        public string? Backend { get; }
        public bool? SupportsOffThreadResourceCreation { get; }
        public bool SupportsDxt1 { get; }
        public bool SupportsS3tc { get; }
        public bool SupportsBc7 { get; }
        public bool SupportsHardwareInstancing { get; }
        public bool SupportsNoOverwrite { get; }
        public bool SupportsSrgbRenderTargets { get; }
        public int MaxTextureSlots { get; }
        public int MaxVertexTextureSlots { get; }

        /// <summary>
        /// Whether a driver of this name parks off-thread resource creation until the next present.
        /// A property of FNA3D's driver implementations, not of the device — <c>ForceToMainThread</c>
        /// exists on 19 entry points in <c>FNA3D_Driver_OpenGL.c</c> and has no counterpart in the
        /// Vulkan or D3D11 drivers.
        ///
        /// <para>Null for an unrecognised or absent name, which includes automatic selection. Do
        /// not "improve" that into a default of true: the whole value of this flag is that it is
        /// only ever stated when it is known.</para>
        /// </summary>
        public static bool? OffThreadResourceCreationFor(string? driverName)
        {
            if (string.IsNullOrEmpty(driverName))
            {
                return null;
            }

            if (string.Equals(driverName, "OpenGL", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (string.Equals(driverName, "Vulkan", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(driverName, "D3D11", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return null;
        }

        internal string Serialise() =>
            "backend=" + (Backend ?? "") + "\n" +
            "offthread=" + Tri(SupportsOffThreadResourceCreation) + "\n" +
            "dxt1=" + Bit(SupportsDxt1) + "\n" +
            "s3tc=" + Bit(SupportsS3tc) + "\n" +
            "bc7=" + Bit(SupportsBc7) + "\n" +
            "instancing=" + Bit(SupportsHardwareInstancing) + "\n" +
            "nooverwrite=" + Bit(SupportsNoOverwrite) + "\n" +
            "srgbtargets=" + Bit(SupportsSrgbRenderTargets) + "\n" +
            "texslots=" + MaxTextureSlots.ToString(CultureInfo.InvariantCulture) + "\n" +
            "vtexslots=" + MaxVertexTextureSlots.ToString(CultureInfo.InvariantCulture) + "\n";

        private static string Bit(bool value) => value ? "1" : "0";

        private static string Tri(bool? value) => value == null ? "?" : Bit(value.Value);
    }
}
