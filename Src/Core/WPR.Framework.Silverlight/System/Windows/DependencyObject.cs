using System.Collections.Generic;

namespace WPR.SilverlightCompability
{
    public class DependencyObject
    {
        private Dictionary<DependencyProperty, object?>? _values;

        /// <summary>WP7 <c>DependencyObject.Dispatcher</c>: the object's UI-thread
        /// dispatcher. Games marshal work back onto the UI thread through it — e.g.
        /// <c>((DependencyObject)Deployment.Current).Dispatcher.BeginInvoke(action)</c>.
        /// We return the shared inline dispatcher (runs work immediately).</summary>
        public Threading.Dispatcher Dispatcher => Threading.Dispatcher.Shared;

        public object? GetValue(DependencyProperty dp)
        {
            if (_values != null && _values.TryGetValue(dp, out var v))
                return v;
            return dp.DefaultMetadata.DefaultValue;
        }

        public void SetValue(DependencyProperty dp, object? value)
        {
            object? oldValue = GetValue(dp);
            if (Equals(oldValue, value))
                return;

            _values ??= new Dictionary<DependencyProperty, object?>();
            _values[dp] = value;

            dp.DefaultMetadata.PropertyChangedCallback?.Invoke(
                this, new DependencyPropertyChangedEventArgs(dp, oldValue, value));
        }

        public void ClearValue(DependencyProperty dp)
        {
            if (_values == null || !_values.TryGetValue(dp, out var oldValue))
                return;

            _values.Remove(dp);
            object? newValue = dp.DefaultMetadata.DefaultValue;

            if (!Equals(oldValue, newValue))
            {
                dp.DefaultMetadata.PropertyChangedCallback?.Invoke(
                    this, new DependencyPropertyChangedEventArgs(dp, oldValue, newValue));
            }
        }
    }
}
