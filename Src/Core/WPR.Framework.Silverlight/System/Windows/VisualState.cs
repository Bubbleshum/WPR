using System;
using System.Collections.Generic;

namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.VisualState</c>.</summary>
    /// <remarks>
    /// The <see cref="ContentPropertyAttribute"/> is what lets the XAML reader place a bare
    /// <c>&lt;Storyboard&gt;</c> child; without it the reader has nowhere to put one and throws
    /// <c>XamlParseException</c>, which fails the whole page. See the note on
    /// <see cref="VisualStateGroup"/> for what that cost.
    /// </remarks>
    [ContentProperty("Storyboard")]
    public class VisualState : DependencyObject
    {
        public string? Name { get; set; }
        public Storyboard? Storyboard { get; set; }
    }
}
