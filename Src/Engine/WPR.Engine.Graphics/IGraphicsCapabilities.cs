#nullable enable

namespace WPR.Engine.Graphics
{
    /// <summary>
    /// What the graphics device this launch got can actually do, as facts rather than as a driver
    /// name. "Vulkan" is an implementation; these are the properties a game either gets or does
    /// not, and they are what a compatibility question should be asked in terms of.
    ///
    /// <para><b>Why this can live in the engine tier when <c>IGraphicsBackend</c> cannot.</b> The
    /// RHI seam speaks <c>Texture2D</c> and <c>GraphicsDevice</c> — game-facing identities the
    /// patcher rescopes into <c>WPR.Framework.Xna</c>, which also consumes the seam, so a contract
    /// naming them is un-invertible and has to stay there. Everything here is an <c>int</c>, a
    /// <c>bool</c> or a <c>string</c>, which is the same exemption <c>Audio3DParams</c> used to
    /// escape to <c>System.Numerics.Vector3</c>. <b>Keep it that way</b>: the moment a member here
    /// names an XNA type this file has to move back.</para>
    ///
    /// <para><b>This is the DEVICE model, and it is not the same thing as
    /// <c>ProfileCapabilities</c>.</b> That type (in <c>WPR.Framework.Xna</c>) is XNA 4.0's own
    /// capability model and is <i>hardcoded per <c>GraphicsProfile</c></i> — Reach guarantees a
    /// 2048 texture limit, 16 samplers, shader model 2.0, and so on. It describes the contract a
    /// game was compiled against and is used to validate what the game asks for. This describes
    /// the hardware actually underneath. Do not cross-wire them: a device meeting Reach's minimums
    /// is a separate question from what the device can do, and conflating the two would make a
    /// diagnostic screen claim a compatibility verdict it never computed.</para>
    ///
    /// <para><b>Only measurable facts belong here.</b> FNA3D's whole capability surface is eight
    /// functions, so properties like the GL ES version, maximum texture size or multiple-render-
    /// target support are deliberately <i>absent</i> rather than present-and-guessed — adding them
    /// needs new FNA3D entry points. A row that is secretly a constant is worse than no row.</para>
    /// </summary>
    public interface IGraphicsCapabilities
    {
        /// <summary>
        /// The FNA3D driver that served the launch — <c>"Vulkan"</c>, <c>"OpenGL"</c>,
        /// <c>"D3D11"</c> — or null when automatic selection won and only FNA3D's own log names
        /// the result.
        /// </summary>
        string? Backend { get; }

        /// <summary>
        /// Whether a thread other than the device thread may create GPU resources without being
        /// parked until the next frame is presented.
        ///
        /// <para><b>This is the capability with a real bug behind it.</b> FNA3D's OpenGL driver
        /// marshals every off-thread resource call onto the device thread and blocks the caller on
        /// a semaphore, drained only by <c>OPENGL_SwapBuffers</c> — so a title whose game thread
        /// waits on its own loader thread deadlocks outright, with no crash, no log, and one core
        /// spinning. Vulkan and D3D11 have no such queue. That is Need for Speed: Undercover,
        /// Fable: Coin Golf and Game Room: Pitfall!, and it is why "which driver" is the wrong
        /// question and "can a worker thread load content" is the right one.</para>
        ///
        /// <para>Null when <see cref="Backend"/> is null: automatic selection means FNA3D chose
        /// from its own table and we were not told which, so the honest answer is that we do not
        /// know rather than a guess that happens to be right on the desktop.</para>
        /// </summary>
        bool? SupportsOffThreadResourceCreation { get; }

        /// <summary>DXT1 block-compressed textures. Part of what XNA's Reach profile assumes, so a
        /// device without it will fail content loads rather than merely look worse.</summary>
        bool SupportsDxt1 { get; }

        /// <summary>DXT3/DXT5 (S3TC) block-compressed textures — the other half of the Reach
        /// assumption above.</summary>
        bool SupportsS3tc { get; }

        /// <summary>BC7 block-compressed textures. No WP7 title ships these; reported because
        /// FNA3D answers it and it costs nothing.</summary>
        bool SupportsBc7 { get; }

        /// <summary>Hardware geometry instancing.</summary>
        bool SupportsHardwareInstancing { get; }

        /// <summary>The <c>NoOverwrite</c> dynamic-buffer lock hint.</summary>
        bool SupportsNoOverwrite { get; }

        /// <summary>sRGB render targets.</summary>
        bool SupportsSrgbRenderTargets { get; }

        /// <summary>Texture slots available to the pixel shader.</summary>
        int MaxTextureSlots { get; }

        /// <summary>Texture slots available to the vertex shader. Zero on Reach-class hardware,
        /// which is normal rather than a fault.</summary>
        int MaxVertexTextureSlots { get; }
    }
}
