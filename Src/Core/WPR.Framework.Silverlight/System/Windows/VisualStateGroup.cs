using System;
using System.Collections.Generic;

namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.VisualStateGroup</c>. Holds named visual states.</summary>
    /// <remarks>
    /// <para><b>Declaring the content property is what makes a <c>&lt;VisualStateGroup&gt;</c>
    /// parse at all.</b> Its states are written as bare children —
    /// <c>&lt;VisualState x:Name="Progress"&gt;</c> — and with no <see cref="ContentPropertyAttribute"/>
    /// the XAML reader has nowhere to put them and throws, which fails the page rather than the
    /// group.</para>
    ///
    /// <para>Carcassonne is the measured case, and it shows that a missing state is not a cosmetic
    /// loss: its splash screen resolves <c>Progress.Storyboard</c> and hangs its <c>Completed</c>
    /// handler off it, and that handler is the only thing that navigates to the main menu. With
    /// the group unparsed, <c>Progress</c> was null, the page drew at full rate and the game could
    /// never leave its splash screen.</para>
    /// </remarks>
    [ContentProperty("States")]
    public class VisualStateGroup : DependencyObject
    {
        public string? Name { get; set; }
        public IList<VisualState> States { get; } = new List<VisualState>();
        public IList<VisualTransition> Transitions { get; } = new List<VisualTransition>();
        public VisualState? CurrentState { get; internal set; }

        public event EventHandler<VisualStateChangedEventArgs>? CurrentStateChanging;
        public event EventHandler<VisualStateChangedEventArgs>? CurrentStateChanged;

        /// <summary>Raised by <see cref="VisualStateManager.GoToState"/> before the state changes.</summary>
        /// <remarks>
        /// A throwing game handler must not abort the transition — the state change is what the
        /// caller asked for, and leaving the group half-moved is worse than a lost notification.
        /// </remarks>
        internal void RaiseCurrentStateChanging(VisualState? from, VisualState to)
        {
            try { CurrentStateChanging?.Invoke(this, new VisualStateChangedEventArgs { OldState = from, NewState = to }); }
            catch { }
        }

        /// <summary>Raised by <see cref="VisualStateManager.GoToState"/> after the state changes.</summary>
        internal void RaiseCurrentStateChanged(VisualState? from, VisualState to)
        {
            try { CurrentStateChanged?.Invoke(this, new VisualStateChangedEventArgs { OldState = from, NewState = to }); }
            catch { }
        }
    }
}
