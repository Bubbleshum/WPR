namespace Microsoft.Phone.Tasks
{
    /// <summary>
    /// Shim for <c>Microsoft.Phone.Tasks.MarketplaceReviewTask</c>.
    ///
    /// Upstream this launches the Marketplace app focused on the review pane for the
    /// currently-running app. On the desktop host there is no Marketplace to launch,
    /// so <see cref="Show"/> is a no-op.
    ///
    /// The shim exists primarily so games whose menu/click code path mentions
    /// <c>MarketplaceReviewTask</c> don't trip a <see cref="System.TypeLoadException"/>
    /// when the JIT walks the method body — which symptomatically appears as a frozen
    /// menu / "missing graphics" because Game.Update is throwing every frame before
    /// the state machine can advance (observed on Fling).
    /// </summary>
    public class MarketplaceReviewTask
    {
        public void Show()
        {
            // Intentionally empty. No Marketplace exists on the desktop host.
        }
    }
}
