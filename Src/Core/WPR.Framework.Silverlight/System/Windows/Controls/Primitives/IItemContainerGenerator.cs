namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Shim for <c>System.Windows.Controls.Primitives.IItemContainerGenerator</c>, what
    /// <see cref="VirtualizingPanel.ItemContainerGenerator"/> returns. Callers cast it to
    /// <see cref="ItemContainerGenerator"/> for the container lookups (LazyListBox does).
    /// </summary>
    /// <remarks>
    /// Silverlight's members here (StartAt / GenerateNext / GeneratorPosition) are the protocol a
    /// custom virtualizing panel uses to realise containers itself. WPR realises every container
    /// up front, so no panel can drive generation and they are not declared.
    /// </remarks>
    public interface IItemContainerGenerator
    {
    }
}
