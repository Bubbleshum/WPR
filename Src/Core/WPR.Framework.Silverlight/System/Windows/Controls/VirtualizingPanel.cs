namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Shim for <c>System.Windows.Controls.VirtualizingPanel</c>, the base of an items panel that
    /// realises containers on demand. WPR realises every container (see
    /// <see cref="IItemContainerGenerator"/>), so this is an ordinary panel with a link back to
    /// the control it hosts the containers of.
    /// </summary>
    public abstract class VirtualizingPanel : Panel
    {
        /// <summary>The owning ItemsControl's generator, as in Silverlight. Null when the panel is
        /// not any control's items panel.</summary>
        public IItemContainerGenerator? ItemContainerGenerator => ItemsOwner?.ItemContainerGenerator;
    }
}
