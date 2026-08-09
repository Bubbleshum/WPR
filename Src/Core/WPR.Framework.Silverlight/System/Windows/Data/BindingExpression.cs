using System;
using System.ComponentModel;
using System.Reflection;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// A live binding attached to a target FrameworkElement and a target DP.
    /// One-way only in 1.5: source's value flows to target; INotifyPropertyChanged on
    /// the source triggers re-evaluation. The source is the binding's explicit Source
    /// if set, otherwise the closest non-null DataContext walking up from the target.
    /// </summary>
    internal class BindingExpression
    {
        private readonly FrameworkElement _target;
        private readonly DependencyProperty _targetProperty;
        private readonly Binding _binding;

        private object? _subscribedSource;
        private string[] _pathSegments = Array.Empty<string>();

        public BindingExpression(FrameworkElement target, DependencyProperty targetProperty, Binding binding)
        {
            _target = target;
            _targetProperty = targetProperty;
            _binding = binding;
            _pathSegments = string.IsNullOrEmpty(binding.Path)
                ? Array.Empty<string>()
                : binding.Path.Split('.');
        }

        public Binding Binding => _binding;
        public DependencyProperty TargetProperty => _targetProperty;

        public void Refresh()
        {
            object? source = ResolveSource();
            HookSource(source);

            // If we can't reach a value, revert the DP to its registered default rather
            // than smashing in a null (which would, e.g., turn an empty-string default
            // for TextBlock.Text into a real null).
            if (source == null)
            {
                Console.WriteLine(
                    $"[Binding] '{_binding.Path}' on {_target.GetType().Name}.{_targetProperty.Name} — source null");
                _target.ClearValue(_targetProperty);
                return;
            }

            object? value = WalkPath(source, _pathSegments);
            if (value == null)
            {
                Console.WriteLine(
                    $"[Binding] '{_binding.Path}' on {_target.GetType().Name}.{_targetProperty.Name} — path resolved to null (source={source.GetType().Name})");
                _target.ClearValue(_targetProperty);
                return;
            }

            object? converted = CoerceToTargetType(value, _targetProperty.PropertyType);
            _target.SetValue(_targetProperty, converted);
        }

        public void Detach()
        {
            HookSource(null);
        }

        private object? ResolveSource()
        {
            if (_binding.Source != null) return _binding.Source;
            for (UIElement? el = _target; el != null; el = el.Parent)
            {
                if (el is FrameworkElement fe && fe.DataContext != null)
                    return fe.DataContext;
            }
            return null;
        }

        private void HookSource(object? source)
        {
            if (ReferenceEquals(_subscribedSource, source)) return;

            if (_subscribedSource is INotifyPropertyChanged oldInpc)
                oldInpc.PropertyChanged -= OnSourcePropertyChanged;

            _subscribedSource = source;

            if (source is INotifyPropertyChanged inpc)
                inpc.PropertyChanged += OnSourcePropertyChanged;
        }

        private void OnSourcePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            // Match against the first path segment (a one-segment property change covers our case
            // exactly; for dotted paths we conservatively re-evaluate on any first-segment match).
            if (_pathSegments.Length == 0) return;
            if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == _pathSegments[0])
                Refresh();
        }

        private static object? WalkPath(object? source, string[] segments)
        {
            object? current = source;
            for (int i = 0; i < segments.Length; i++)
            {
                if (current == null) return null;
                Type t = current.GetType();

                // Try instance property first (typical), then a static property on
                // the same type (the auto-generated .resx accessor classes —
                // AppResources etc. — expose every string as a static property
                // even though XAML bindings access them via instance.Path).
                PropertyInfo? p = t.GetProperty(segments[i],
                    BindingFlags.Public | BindingFlags.Instance);
                if (p != null)
                {
                    current = p.GetValue(current);
                    continue;
                }
                p = t.GetProperty(segments[i],
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                if (p != null)
                {
                    current = p.GetValue(null);
                    continue;
                }

                // Fallback: a public field (rare in WP code but cheap to check).
                FieldInfo? f = t.GetField(segments[i],
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy);
                if (f != null)
                {
                    current = f.GetValue(f.IsStatic ? null : current);
                    continue;
                }
                return null;
            }
            return current;
        }

        private static object? CoerceToTargetType(object? value, Type target)
        {
            if (value == null) return null;
            if (target.IsAssignableFrom(value.GetType())) return value;
            // Light-touch coercion: numeric/string round-trip via XamlTypeConverter when possible.
            try
            {
                if (value is string s) return XamlTypeConverter.Convert(s, target);
                return Convert.ChangeType(value, target);
            }
            catch
            {
                return value; // hand it through; the SetValue may throw with a clearer message
            }
        }
    }
}
