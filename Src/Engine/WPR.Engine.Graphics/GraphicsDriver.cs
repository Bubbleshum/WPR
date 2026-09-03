#nullable enable
namespace WPR.Engine.Graphics
{
    /// <summary>
    /// Which FNA3D driver a platform wants. Declared by a platform head through
    /// <c>IPlatformCapabilities.GraphicsDriver</c>; resolved and applied at launch.
    ///
    /// <para>Names match FNA3D's own driver <c>Name</c> strings, which it compares with
    /// <c>strcmp</c> — a name no compiled-in driver matches means device creation fails outright
    /// rather than falling back. The compiled-in set differs per platform: Windows has D3D11 and
    /// OpenGL, Android has OpenGL and Vulkan.</para>
    /// </summary>
    public enum GraphicsDriver
    {
        /// <summary>
        /// The platform has no opinion — <b>do not touch the driver lever at all</b>.
        ///
        /// <para>Distinct from <see cref="Automatic"/> on purpose, and the distinction is
        /// load-bearing. This leaves whatever the process environment asked for in place, which is
        /// how Android's <c>fna3d.env</c> force survives; and it is the desktop's behaviour, which
        /// never set the hint at all. <see cref="Automatic"/> actively CLEARS a force that would
        /// otherwise apply. Defaulting to this means a head that declares nothing behaves exactly
        /// as it did before there was a capability model.</para>
        /// </summary>
        Unspecified = 0,

        /// <summary>Explicitly clear any forced driver and let FNA3D walk its table in order.</summary>
        Automatic,

        OpenGL,
        Vulkan,
        D3D11,
    }
}
