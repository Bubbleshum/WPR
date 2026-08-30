using Microsoft.Xna.Framework.Graphics;

namespace WPR.Xna.Compat
{
    /// <summary>
    /// WP7 display-mode override for <see cref="Microsoft.Xna.Framework.Graphics.GraphicsAdapter"/>.
    /// The <see cref="GraphicsDevice"/> story, one level down: a <c>MemberPatches</c> target whose
    /// only job is to answer <c>get_CurrentDisplayMode()</c> with the fixed 480x800 WP7 panel
    /// rather than the real adapter mode the RHI reports.
    ///
    /// <para>Note this declares a literal <c>get_CurrentDisplayMode()</c> *method*, not a property.
    /// That is deliberate and load-bearing: <c>MemberPatches</c> matches on the memberref signature
    /// and only rewrites its declaring type, so the member name has to survive verbatim.</para>
    /// </summary>
    public class GraphicsAdapter : Microsoft.Xna.Framework.Graphics.GraphicsAdapter
    {
        public GraphicsAdapter(
            DisplayModeCollection modes,
            string name,
            string description
        )
            : base(modes, name, description)
        {
        }

        public DisplayMode get_CurrentDisplayMode()
        {
            return new DisplayMode(480, 800, CurrentDisplayMode.Format);
        }
    }
}
