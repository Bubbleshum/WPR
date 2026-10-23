namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Controls.Primitives.ManipulationState</c>: whether a
    /// scrolling control is idle, under a finger, or coasting.</summary>
    public enum ManipulationState
    {
        Idle = 0,
        Manipulating = 1,
        Animating = 2,
    }
}
