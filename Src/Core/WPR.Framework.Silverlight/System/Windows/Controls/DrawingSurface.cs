namespace WPR.SilverlightCompability
{
    /// <summary>Shim for WP8's <c>System.Windows.Controls.DrawingSurface</c>.</summary>
    /// <remarks>
    /// <para>An element a native component renders into, with a texture it hands back per frame.
    /// A Direct3D/XAML game whose page carries one is hosted by the WP8 native runtime instead
    /// (<c>NativeXamlComponent</c>), which plays this element's part itself.</para>
    /// <para>What reaches here is a MANAGED app that puts one in a corner of its XAML: Microsoft
    /// Bingo draws its particle effects (FrimaStudio's Icewave, native ARM) into one inside a
    /// user control. A managed title cannot call into the ARM component yet, so the provider and
    /// handler are only remembered and the element paints nothing - the rest of the page is
    /// unaffected. Before the type existed, every page naming it failed to load.</para>
    /// </remarks>
    public class DrawingSurface : FrameworkElement
    {
        /// <summary>The content provider the app handed over.</summary>
        internal object? AttachedContentProvider { get; private set; }

        /// <summary>The manipulation handler the app handed over.</summary>
        internal object? AttachedManipulationHandler { get; private set; }

        public void SetContentProvider(object? contentProvider) => AttachedContentProvider = contentProvider;

        public void SetManipulationHandler(object? manipulationHandler) => AttachedManipulationHandler = manipulationHandler;
    }
}
