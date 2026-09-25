namespace Microsoft.Xna.Framework.Graphics
{
    /// <summary>
    /// Shim for <c>Microsoft.Xna.Framework.Graphics.GraphicsDeviceExtensions</c>, from the
    /// WP7.1 <c>Microsoft.Xna.Framework.Interop</c> assembly.
    /// </summary>
    public static class GraphicsDeviceExtensions
    {
        /// <summary>
        /// On the phone, hands the backbuffer back and forth between the Silverlight compositor
        /// and XNA: <c>true</c> means "XNA is drawing now, Silverlight keep off", <c>false</c>
        /// means the reverse.
        /// </summary>
        /// <remarks>
        /// <b>Deliberately a no-op under WPR, and that is not a stub to be filled in later.</b>
        /// The mode exists because a real WP7 device runs one GPU surface under two renderers.
        /// WPR's mixed-mode host has no Silverlight compositor competing for the device — the
        /// host game owns the backbuffer for the whole launch and the Silverlight tree is never
        /// rendered at all (in every title that uses this, the page's own XAML says as much:
        /// "No XAML content is required as the page is rendered entirely with the XNA
        /// Framework"). There is no second owner to hand anything to.
        ///
        /// <para>Honouring it would actively break these games. The call pattern is
        /// <c>SetSharingMode(true)</c> at the top of a draw and <c>SetSharingMode(false)</c> at
        /// the bottom — Cut the Rope also calls <c>false</c> from <c>OnNavigatedFrom</c> and on
        /// its error path — so any implementation that released or invalidated the device on
        /// <c>false</c> would tear it down after the first frame.</para>
        ///
        /// <para>12 of the 13 mixed-mode titles in the library call this, all in that shape.</para>
        /// </remarks>
        public static void SetSharingMode(this GraphicsDevice device, bool sharingMode)
        {
            // No-op by design. See the remarks above before changing this.
        }
    }
}
