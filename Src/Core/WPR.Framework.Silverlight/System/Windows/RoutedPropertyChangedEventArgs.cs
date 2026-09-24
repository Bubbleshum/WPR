namespace WPR.SilverlightCompability
{
    /// <summary>
    /// Shim for <c>System.Windows.RoutedPropertyChangedEventArgs&lt;T&gt;</c> — what
    /// <see cref="RangeBase.ValueChanged"/> and the toolkit's value-changed events carry.
    /// </summary>
    public class RoutedPropertyChangedEventArgs<T> : RoutedEventArgs
    {
        public RoutedPropertyChangedEventArgs(T oldValue, T newValue)
        {
            OldValue = oldValue;
            NewValue = newValue;
        }

        public T OldValue { get; }
        public T NewValue { get; }
    }
}
