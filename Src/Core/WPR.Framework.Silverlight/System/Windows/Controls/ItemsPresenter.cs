namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Controls.ItemsPresenter</c>: the place in an
    /// ItemsControl's template where its items panel goes.</summary>
    /// <remarks>
    /// A Panel here (Silverlight's derives from FrameworkElement) holding the items panel as its
    /// one child, so the layout pass, rasteriser, hit tester and Loaded walk reach the containers
    /// through it with no case of their own. It fills its slot, as the panel does in Silverlight.
    /// </remarks>
    public class ItemsPresenter : Panel
    {
        internal override Size WprArrange(Size finalSize)
        {
            foreach (UIElement child in Children)
                child.Arrange(new Rect(0, 0, finalSize.Width, finalSize.Height));
            return finalSize;
        }
    }
}
