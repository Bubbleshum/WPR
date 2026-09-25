using System;
using System.Collections;
using System.Collections.Generic;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Shim for <c>System.Windows.VisualStateManager</c>.
    /// </summary>
    /// <remarks>
    /// <para><b><see cref="GoToState"/> really runs the state's storyboard.</b> It used to be
    /// <c>return false</c> — "no transition occurred" — on the reasoning that callers treat that as
    /// "skip the animation". Plenty do. The ones that do not are stuck for ever, because a WP7 page
    /// routinely uses a state's storyboard as its *clock*: it enters a state and hangs its next
    /// step off the storyboard's <c>Completed</c>.</para>
    ///
    /// <para>Carcassonne's splash screen is exactly that, and it is why the game could never
    /// start:</para>
    /// <code>
    /// VisualStateManager.GoToState(this, Progress.Name, true);
    /// ((Timeline)Progress.Storyboard).Completed += SplashScreenPage_Finished;  // navigates to MainMenu
    /// </code>
    /// <para>With a no-op <c>GoToState</c> the storyboard never ran, <c>Completed</c> never fired,
    /// and the splash drew at full frame rate for ever with no exception anywhere.</para>
    ///
    /// <para>This became implementable only once <c>Storyboard.Begin()</c> and
    /// <c>AnimationClock</c> were real; against the old no-op <c>Begin()</c> it would have changed
    /// nothing.</para>
    /// </remarks>
    public class VisualStateManager : DependencyObject
    {
        /// <summary>Depth limit for the search below; nothing legitimate nests this far.</summary>
        private const int MaxSearchDepth = 32;

        public static readonly DependencyProperty VisualStateGroupsProperty =
            DependencyProperty.RegisterAttached("VisualStateGroups", typeof(IList<VisualStateGroup>),
                typeof(VisualStateManager), new PropertyMetadata((object?)null));

        /// <summary>
        /// The state groups attached to <paramref name="obj"/>, or null.
        /// </summary>
        /// <remarks>
        /// Tolerates a single <see cref="VisualStateGroup"/> in the slot as well as a list. The
        /// XAML reader's attached-property element path materialises the first child element and
        /// stores that, so a lone <c>&lt;VisualStateGroup&gt;</c> — the common case — arrives here
        /// unwrapped, and a strict cast would answer null for the very shape that occurs most.
        /// </remarks>
        public static IList<VisualStateGroup>? GetVisualStateGroups(DependencyObject obj)
        {
            object? raw = obj?.GetValue(VisualStateGroupsProperty);
            switch (raw)
            {
                case null:
                    return null;
                case IList<VisualStateGroup> list:
                    return list;
                case VisualStateGroup single:
                    return new List<VisualStateGroup> { single };
                case IEnumerable seq:
                {
                    var collected = new List<VisualStateGroup>();
                    foreach (object? item in seq)
                        if (item is VisualStateGroup g) collected.Add(g);
                    return collected.Count > 0 ? collected : null;
                }
                default:
                    return null;
            }
        }

        public static void SetVisualStateGroups(DependencyObject obj, IList<VisualStateGroup> value)
            => obj?.SetValue(VisualStateGroupsProperty, value);

        /// <summary>
        /// Moves <paramref name="control"/> to the named visual state, starting that state's
        /// storyboard and stopping the one it leaves. Returns false when no such state exists.
        /// </summary>
        /// <remarks>
        /// <paramref name="useTransitions"/> is accepted and ignored: a
        /// <see cref="VisualTransition"/>'s own storyboard is a crossfade between states, and
        /// running it would need the two to be composed. The state storyboard — the part games use
        /// as a clock — is what matters, and skipping the transition is what
        /// <c>useTransitions: false</c> already means.
        /// </remarks>
        public static bool GoToState(Control control, string stateName, bool useTransitions)
        {
            if (control == null || string.IsNullOrEmpty(stateName)) return false;

            try
            {
                foreach (DependencyObject candidate in SelfAndDescendants(control, MaxSearchDepth))
                {
                    IList<VisualStateGroup>? groups = GetVisualStateGroups(candidate);
                    if (groups == null) continue;

                    foreach (VisualStateGroup group in groups)
                    {
                        if (group == null) continue;

                        foreach (VisualState state in group.States)
                        {
                            if (state == null) continue;
                            if (!string.Equals(state.Name, stateName, StringComparison.Ordinal)) continue;

                            Transition(group, state);
                            return true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[VisualStateManager] GoToState('{stateName}') failed: {ex.Message}");
                return false;
            }

            return false;
        }

        private static void Transition(VisualStateGroup group, VisualState state)
        {
            VisualState? from = group.CurrentState;
            if (ReferenceEquals(from, state))
            {
                // Already here. Silverlight answers true without restarting the storyboard, and
                // restarting would retrigger any Completed handler hung off it.
                return;
            }

            group.RaiseCurrentStateChanging(from, state);

            // Stop the outgoing state's storyboard before starting the incoming one, so two
            // states cannot animate the same property at once.
            try { from?.Storyboard?.Stop(); } catch { /* a game handler may throw; not ours to fail on */ }

            group.CurrentState = state;

            try { state.Storyboard?.Begin(); } catch { }

            group.RaiseCurrentStateChanged(from, state);
        }

        /// <summary>
        /// The element and its visual descendants, breadth first.
        /// </summary>
        /// <remarks>
        /// The search starts at the control and goes DOWN because that is where the groups are:
        /// XAML attaches <c>VisualStateManager.VisualStateGroups</c> to the layout root inside the
        /// page, while <c>GoToState</c> is handed the page itself. Silverlight looks at the
        /// template root specifically; WPR applies no templates, so it sweeps the subtree instead.
        /// </remarks>
        private static IEnumerable<DependencyObject> SelfAndDescendants(DependencyObject root, int maxDepth)
        {
            var queue = new Queue<(DependencyObject Node, int Depth)>();
            var seen = new HashSet<DependencyObject>();
            queue.Enqueue((root, 0));

            while (queue.Count > 0)
            {
                (DependencyObject node, int depth) = queue.Dequeue();
                if (node == null || !seen.Add(node)) continue;

                yield return node;
                if (depth >= maxDepth) continue;

                foreach (DependencyObject child in ChildrenOf(node))
                    queue.Enqueue((child, depth + 1));
            }
        }

        private static IEnumerable<DependencyObject> ChildrenOf(DependencyObject node)
        {
            switch (node)
            {
                case Panel panel:
                    foreach (UIElement child in panel.Children)
                        if (child != null) yield return child;
                    break;

                case Border border:
                    if (border.Child != null) yield return border.Child;
                    break;

                case ContentControl content:
                    // Presenter rather than Content: a non-UIElement Content is wrapped, and the
                    // wrapper is what is actually in the tree.
                    if (content.Presenter != null) yield return content.Presenter;
                    else if (content.Content is UIElement direct) yield return direct;
                    break;
            }
        }
    }
}
