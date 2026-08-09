namespace WPR.SilverlightCompability
{
    public struct DependencyPropertyChangedEventArgs
    {
        public DependencyProperty Property { get; }
        public object? OldValue { get; }
        public object? NewValue { get; }

        public DependencyPropertyChangedEventArgs(DependencyProperty property, object? oldValue, object? newValue)
        {
            Property = property;
            OldValue = oldValue;
            NewValue = newValue;
        }
    }
}
