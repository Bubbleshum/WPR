using SDL2;

namespace WPR.Backend.FNA
{
    /// <summary>
    /// Chooses which FNA3D graphics driver a game launch uses, overriding whatever the process
    /// environment asked for.
    ///
    /// <para><b>Why this exists.</b> <c>FNA3D_PrepareWindowAttributes</c> walks its driver table in
    /// order and takes the first whose <c>PrepareWindowAttributes</c> succeeds, unless the
    /// <c>FNA3D_FORCE_DRIVER</c> hint names one — in which case it <c>continue</c>s past every other
    /// driver, so forcing is a hard selection with no fallback. Android ships that hint as a real
    /// process environment variable (<c>fna3d.env</c>) set to <c>Vulkan</c>, because automatic order
    /// offers OpenGL first there and that driver marshals every off-thread GPU call onto the device
    /// thread, blocking the caller until the next swap — which deadlocks any game that loads content
    /// on a worker.</para>
    ///
    /// <para><b>Why it has to be a hint and not an env var.</b> The env var cannot be changed from
    /// managed code: .NET's <c>Environment.SetEnvironmentVariable</c> does not propagate to the
    /// native <c>environ</c> on Unix, which is what <c>SDL_getenv</c> reads. But
    /// <c>SDL_GetHint</c> consults its own hint list first and lets a hint set at
    /// <c>SDL_HINT_OVERRIDE</c> priority win over the environment — so that is the one lever that
    /// works after the process has started.</para>
    ///
    /// <para>This type is deliberately policy-free: it knows how to set the lever, not when to. The
    /// platform head decides that — it declares a driver through <c>IPlatformCapabilities</c> and
    /// <c>WPR.Engine.Graphics.GraphicsDriverPreference</c> resolves the declaration against the
    /// per-device <c>fna3d_driver.txt</c> override. Which driver a device wants is not something a
    /// graphics backend should be reasoning about.</para>
    /// </summary>
    public static class GraphicsDriverSelection
    {
        internal const string ForceDriverHint = "FNA3D_FORCE_DRIVER";

        /// <summary>
        /// Forces <paramref name="driverName"/> for the next device creation, or restores automatic
        /// selection when it is null/empty/"auto".
        ///
        /// <para>Must be called before the game constructs its <c>GraphicsDevice</c> — the hint is
        /// read once, inside <c>FNA3D_PrepareWindowAttributes</c>. Names are matched with
        /// <c>strcmp</c> against the driver's own <c>Name</c>, so they are case-sensitive:
        /// <c>"OpenGL"</c>, <c>"Vulkan"</c>, <c>"D3D11"</c>. A name no compiled-in driver matches
        /// means device creation fails outright rather than falling back, which is why the automatic
        /// retry in <c>SDL2_FNAPlatform.PrepareWindowAttributesWithFallback</c> exists.</para>
        /// </summary>
        public static void Apply(string? driverName)
        {
            bool auto = string.IsNullOrWhiteSpace(driverName)
                || string.Equals(driverName!.Trim(), "auto", System.StringComparison.OrdinalIgnoreCase);

            /* Null (not "") clears the hint: an empty string is still a non-NULL value to
             * SDL_GetHint, and would then be strcmp'd against every driver name and match none —
             * i.e. "No supported FNA3D driver found!" rather than the automatic order. */
            string? value = auto ? null : driverName!.Trim();

            SDL.SDL_SetHintWithPriority(
                ForceDriverHint,
                value,
                SDL.SDL_HintPriority.SDL_HINT_OVERRIDE
            );

            WPR.Common.Log.Info(
                WPR.Common.LogCategory.AppList,
                auto
                    ? "[wpr-gfx] FNA3D driver: automatic (force hint cleared at OVERRIDE priority)"
                    : $"[wpr-gfx] FNA3D driver: forced to '{value}'"
            );
        }
    }
}
