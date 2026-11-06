namespace System.Windows.Input
{
    /// <summary>
    /// Shim for <c>System.Windows.Input.FocusManager</c>: which element has keyboard focus.
    /// </summary>
    /// <remarks>
    /// WPR routes no keyboard input to Silverlight pages, so focus is only bookkeeping: the last
    /// control whose <see cref="System.Windows.Controls.Control.Focus"/> succeeded. Marmalade saves
    /// it before showing its own keyboard and gives focus back afterwards.
    /// </remarks>
    public static class FocusManager
    {
        private static WeakReference<object>? _focused;

        public static object? GetFocusedElement() =>
            _focused is not null && _focused.TryGetTarget(out object? element) ? element : null;

        internal static void SetFocusedElement(object element) => _focused = new WeakReference<object>(element);
    }
}
