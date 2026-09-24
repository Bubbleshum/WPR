using System.Collections.Generic;

namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.TriggerAction</c>.</summary>
    public abstract class TriggerAction : DependencyObject
    {
        /// <summary>Performs the action on <paramref name="target"/>, the element that fired.</summary>
        internal abstract void Invoke(FrameworkElement target);
    }

    /// <summary>Shim for <c>System.Windows.Media.Animation.BeginStoryboard</c>.</summary>
    /// <remarks>
    /// The only trigger action Silverlight has, which is why this file carries it rather than
    /// giving it one of its own.
    /// </remarks>
    [ContentProperty(nameof(Storyboard))]
    public class BeginStoryboard : TriggerAction
    {
        public Storyboard? Storyboard { get; set; }

        internal override void Invoke(FrameworkElement target)
        {
            Storyboard? storyboard = Storyboard;
            if (storyboard == null) return;

            // The scope owner is what a Storyboard.TargetName resolves against, and a triggered
            // storyboard is declared inside the element that fires it.
            storyboard.ScopeOwner ??= target;

            try { storyboard.Begin(); }
            catch { /* the game's animation; its failure must not break Loaded */ }
        }
    }

    /// <summary>Shim for <c>System.Windows.TriggerActionCollection</c>.</summary>
    public class TriggerActionCollection : List<TriggerAction> { }

    /// <summary>Shim for <c>System.Windows.TriggerBase</c>.</summary>
    public abstract class TriggerBase : DependencyObject { }

    /// <summary>Shim for <c>System.Windows.TriggerCollection</c>.</summary>
    public class TriggerCollection : List<TriggerBase> { }

    /// <summary>Shim for <c>System.Windows.EventTrigger</c>.</summary>
    /// <remarks>
    /// <para><b>Silverlight supports exactly one routed event here — <c>Loaded</c></b> — so an
    /// EventTrigger is in practice "run this storyboard when the element appears", which is how a
    /// page declares its entrance animation without any code-behind. That makes it worth actually
    /// firing rather than merely parsing: a page whose content animates in from opacity 0 stays
    /// invisible for ever if the trigger never runs.</para>
    ///
    /// <para>Galactic Reign's MenuPage is why the type exists at all — an unresolvable
    /// <c>EventTrigger</c> threw out of the element declaring it and failed the whole page.</para>
    /// </remarks>
    [ContentProperty(nameof(Actions))]
    public class EventTrigger : TriggerBase
    {
        /// <summary>
        /// The event to hook. Kept as the string XAML wrote, because WPR has no
        /// <c>RoutedEvent</c> registry to resolve it against and <c>Loaded</c> is the only value
        /// Silverlight accepts.
        /// </summary>
        public string? RoutedEvent { get; set; }

        public TriggerActionCollection Actions { get; } = new TriggerActionCollection();

        /// <summary>Runs every action. Called when the owning element is loaded.</summary>
        internal void Fire(FrameworkElement target)
        {
            foreach (TriggerAction action in Actions)
            {
                try { action?.Invoke(target); }
                catch { /* one bad action must not stop the rest */ }
            }
        }
    }
}
