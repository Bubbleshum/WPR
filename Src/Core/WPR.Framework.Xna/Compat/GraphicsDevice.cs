using Microsoft.Xna.Framework.Graphics;

namespace WPR.Xna.Compat
{
    /// <summary>
    /// WP7 display-mode override for <see cref="Microsoft.Xna.Framework.Graphics.GraphicsDevice"/>.
    ///
    /// <para>Not a duplicate of the real type and not constructed by WPR: it exists purely as a
    /// <c>MemberPatches</c> target. <c>ApplicationPatcher</c> rewrites the declaring type of every
    /// <c>GraphicsDevice::get_DisplayMode()</c> call in game IL to this class, so games see the
    /// fixed 480x800 WP7 panel instead of the host monitor. Everything else falls through to the
    /// base.</para>
    ///
    /// <para>Lives beside the type it shadows (moved out of the deleted WPR.XnaCompability shim
    /// assembly on 2026-08-29) rather than inside <c>Microsoft.Xna.Framework.Graphics</c>, so it
    /// cannot be mistaken for part of the emulated XNA surface — <c>WPR.Xna.Compat</c> is WPR's
    /// own namespace, like the <c>WPR.Xna.Rhi</c> backend seam next door. The 480x800 constant
    /// matches the one PresentationParameters already inlines for the same reason.</para>
    /// </summary>
    public class GraphicsDevice : Microsoft.Xna.Framework.Graphics.GraphicsDevice
    {
        public GraphicsDevice(Microsoft.Xna.Framework.Graphics.GraphicsAdapter adapter, GraphicsProfile graphicsProfile, PresentationParameters presentationParameters) 
            : base(adapter, graphicsProfile, presentationParameters)
        {
        }

        public new DisplayMode DisplayMode => new DisplayMode(480, 800, base.DisplayMode.Format);
    }
}
