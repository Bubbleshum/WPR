using System;
using System.Collections.Generic;

namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.VisualStateGroup</c>. Holds named visual states.</summary>
    public class VisualStateGroup : DependencyObject
    {
        public string? Name { get; set; }
        public IList<VisualState> States { get; } = new List<VisualState>();
        public IList<VisualTransition> Transitions { get; } = new List<VisualTransition>();
        public VisualState? CurrentState { get; internal set; }

#pragma warning disable CS0067
        public event EventHandler<VisualStateChangedEventArgs>? CurrentStateChanging;
        public event EventHandler<VisualStateChangedEventArgs>? CurrentStateChanged;
#pragma warning restore CS0067
    }
}
