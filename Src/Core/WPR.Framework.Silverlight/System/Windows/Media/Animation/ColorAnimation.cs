using System;

namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Media.Animation.ColorAnimation</c>.</summary>
    /// <remarks>
    /// <para>The colour counterpart of <see cref="DoubleAnimation"/>, and the same shape: it holds
    /// its endpoints so a storyboard carrying one can be parsed and run. <c>AnimationClock</c>
    /// drives <c>Double</c> targets only, so the colour itself does not yet move — but the timeline
    /// keeps its duration and still raises <c>Completed</c>, which is what games hang their next
    /// step off.</para>
    ///
    /// <para><b>Its absence was not limited to a missing animation.</b> An unresolvable type throws
    /// out of the element that declares it and takes every sibling with it, so the 16
    /// <c>ColorAnimation</c>s in Carcassonne's main menu cost the storyboards that contained them
    /// and the elements those sat on. Worth remembering when a page renders partially: look for an
    /// unresolved type above the missing region, not for a fault in what is missing.</para>
    /// </remarks>
    public class ColorAnimation : Timeline
    {
        public Color? From { get; set; }
        public Color? To { get; set; }
        public Color? By { get; set; }
        public IEasingFunction? EasingFunction { get; set; }
    }
}
