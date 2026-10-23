using System;
using System.ComponentModel;
using System.Reflection;

namespace WPR.SilverlightCompability
{
    /// <summary>
    /// A live binding attached to a target property. One-way only in 1.5: source's value flows
    /// to target; INotifyPropertyChanged on the source triggers re-evaluation. The source is the
    /// binding's explicit Source if set, otherwise the closest non-null DataContext walking up
    /// from the context element.
    /// </summary>
    /// <remarks>
    /// The target is usually the context element itself. It may also be an object inside it that
    /// is not an element — <c>&lt;Image.Source&gt;&lt;BitmapImage UriSource="{Binding Icon}"/&gt;</c>,
    /// <c>&lt;SolidColorBrush Color="{Binding Tint}"/&gt;</c> — which takes the element's
    /// DataContext, as WP8 does; such a target is set through its dependency property when it
    /// has one, else its CLR property.
    /// </remarks>
    internal class BindingExpression
    {
        private readonly FrameworkElement _context;
        private readonly object _target;
        private readonly DependencyProperty? _targetProperty;
        private readonly PropertyInfo? _clrProperty;
        private readonly Binding _binding;

        private object? _subscribedSource;
        private string[] _pathSegments = Array.Empty<string>();

        public BindingExpression(FrameworkElement target, DependencyProperty targetProperty, Binding binding)
            : this(target, target, targetProperty, null, binding)
        {
        }

        internal BindingExpression(FrameworkElement context, object target,
            DependencyProperty? targetProperty, PropertyInfo? clrProperty, Binding binding)
        {
            _context = context;
            _target = target;
            _targetProperty = targetProperty;
            _clrProperty = clrProperty;
            _binding = binding;
            _pathSegments = string.IsNullOrEmpty(binding.PathText)
                ? Array.Empty<string>()
                : binding.PathText.Split('.');
        }

        public Binding Binding => _binding;
        public DependencyProperty? TargetProperty => _targetProperty;

        /// <summary>The object whose property this binding sets.</summary>
        public object Target => _target;

        /// <summary>The property's name, for the log.</summary>
        private string TargetName => _targetProperty?.Name ?? _clrProperty?.Name ?? "?";

        /// <summary>Whether this binding writes <paramref name="property"/> of <paramref name="target"/>.</summary>
        public bool Writes(object target, DependencyProperty? property, PropertyInfo? clrProperty) =>
            ReferenceEquals(_target, target)
            && (property != null ? ReferenceEquals(_targetProperty, property) : Equals(_clrProperty, clrProperty));

        public void Refresh()
        {
            object? source = ResolveSource();
            HookSource(source);

            Type targetType = _targetProperty?.PropertyType ?? _clrProperty!.PropertyType;

            // If we can't reach a value, revert the DP to its registered default rather
            // than smashing in a null (which would, e.g., turn an empty-string default
            // for TextBlock.Text into a real null).
            if (source == null)
            {
                // A binding ON DataContext with nothing above it to read leaves the value alone.
                // The app may have set it from code, which in Silverlight replaces the binding;
                // clearing it here wiped the page's view model (Fox News / PointAbout Mashup drew
                // nothing).
                if (ReferenceEquals(_targetProperty, FrameworkElement.DataContextProperty) && ReferenceEquals(_target, _context))
                    return;
                Console.WriteLine(
                    $"[Binding] '{_binding.PathText}' on {_target.GetType().Name}.{TargetName} — source null");
                SetOrClear(_binding.FallbackValue, targetType);
                return;
            }

            object? value = WalkPath(source, _pathSegments, out bool resolved);
            if (_binding.Converter != null && resolved)
            {
                try
                {
                    value = _binding.Converter.Convert(value, targetType, _binding.ConverterParameter,
                        _binding.ConverterCulture ?? System.Globalization.CultureInfo.CurrentUICulture);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Trace.WriteLine(
                        $"[Binding] '{_binding.PathText}' converter {_binding.Converter.GetType().Name} threw: {ex.Message}");
                    return;
                }
            }

            if (value == null)
            {
                Console.WriteLine(
                    $"[Binding] '{_binding.PathText}' on {_target.GetType().Name}.{TargetName} — path resolved to null (source={source.GetType().Name})");
                SetOrClear(resolved ? _binding.TargetNullValue : _binding.FallbackValue, targetType);
                return;
            }

            object? converted = CoerceToTargetType(value, targetType);
            if (ReferenceEquals(converted, NotConvertible)) return;
            Set(converted);
        }

        private void Set(object? value)
        {
            if (_targetProperty != null && _target is DependencyObject d) d.SetValue(_targetProperty, value);
            else _clrProperty?.SetValue(_target, value);
        }

        /// <summary>Sets a fallback (FallbackValue / TargetNullValue) when one was given, else clears.</summary>
        private void SetOrClear(object? fallback, Type targetType)
        {
            if (fallback != null)
            {
                object? converted = CoerceToTargetType(fallback, targetType);
                if (!ReferenceEquals(converted, NotConvertible))
                {
                    Set(converted);
                    return;
                }
            }

            Clear();
        }

        private void Clear()
        {
            if (_targetProperty != null && _target is DependencyObject d) d.ClearValue(_targetProperty);
            // A CLR property has no default to go back to; it keeps what it had.
        }

        public void Detach()
        {
            HookSource(null);
            UnhookPath();
        }

        private object? ResolveSource()
        {
            if (_binding.Source != null) return _binding.Source;
            // A binding ON DataContext takes its source from the context the element inherits,
            // i.e. from its parent, as in Silverlight. Starting at the element itself read back
            // the value the binding had just written: the path then missed, the binding cleared
            // it, the parent's context came back, and so on until the stack overflowed (YouTube's
            // page templates bind DataContext="{Binding …}").
            UIElement? start = ReferenceEquals(_targetProperty, FrameworkElement.DataContextProperty)
                               && ReferenceEquals(_target, _context)
                ? _context.Parent as UIElement
                : _context;
            for (UIElement? el = start; el != null; el = el.Parent)
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
            if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == MemberOf(_pathSegments[0]))
                Refresh();
        }

        // The objects part-way along a dotted path ("Model.ScreenSize": the Model), each watched for
        // a change of the NEXT segment. Without these, a binding heard only its source's first
        // property: Microsoft Minesweeper's tutorial sets Model.ScreenSize and Model.SkipText after
        // the bindings were made, and its text boxes stayed 0 wide and its buttons empty.
        private readonly System.Collections.Generic.List<(INotifyPropertyChanged Owner, string Segment)> _pathHooks = new();

        // Collections an indexer in the path read from ("FavoriteModules[0]"): a change to one
        // can change what the index names.
        private readonly System.Collections.Generic.List<System.Collections.Specialized.INotifyCollectionChanged> _collectionHooks = new();

        private void UnhookPath()
        {
            foreach ((INotifyPropertyChanged owner, _) in _pathHooks)
                owner.PropertyChanged -= OnPathPropertyChanged;
            _pathHooks.Clear();
            foreach (var collection in _collectionHooks)
                collection.CollectionChanged -= OnPathCollectionChanged;
            _collectionHooks.Clear();
        }

        private void HookPath(object? owner, string segment)
        {
            if (owner is not INotifyPropertyChanged inpc) return;
            _pathHooks.Add((inpc, MemberOf(segment)));
            inpc.PropertyChanged += OnPathPropertyChanged;
        }

        private void OnPathCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => Refresh();

        /// <summary>The property a path segment names: "Items" of "Items[0]"; "" for "[0]".</summary>
        private static string MemberOf(string segment)
        {
            int bracket = segment.IndexOf('[');
            return bracket < 0 ? segment : segment.Substring(0, bracket);
        }

        private void OnPathPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            foreach ((INotifyPropertyChanged owner, string segment) in _pathHooks)
            {
                if (ReferenceEquals(owner, sender) && (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == segment))
                {
                    Refresh();
                    return;
                }
            }
        }

        /// <summary>
        /// The value at the end of <paramref name="segments"/>; <paramref name="resolved"/> is false
        /// when a link in the path was missing (as opposed to the last property being null).
        /// Hooks every intermediate object for changes.
        /// </summary>
        private object? WalkPath(object? source, string[] segments, out bool resolved)
        {
            UnhookPath();
            resolved = true;
            object? current = source;
            for (int i = 0; i < segments.Length; i++)
            {
                if (current == null) { resolved = false; return null; }
                if (i > 0) HookPath(current, segments[i]);

                // "FavoriteModules[0]": the property, then each index in turn (Silverlight's
                // indexer path syntax; Fox News binds each PivotItem's DataContext this way).
                string member = MemberOf(segments[i]);
                if (member.Length != segments[i].Length)
                {
                    if (member.Length > 0 && !TryReadMember(ref current, member)) { resolved = false; return null; }
                    if (!ApplyIndexers(ref current, segments[i].Substring(member.Length))) { resolved = false; return null; }
                    continue;
                }

                if (TryReadMember(ref current, segments[i])) continue;
                resolved = false;
                return null;
            }
            return current;
        }

        /// <summary>Reads property or field <paramref name="name"/> of <paramref name="current"/>.</summary>
        private static bool TryReadMember(ref object? current, string name)
        {
            if (current == null) return false;
            Type t = current.GetType();

            // Try instance property first (typical), then a static property on
            // the same type (the auto-generated .resx accessor classes —
            // AppResources etc. — expose every string as a static property
            // even though XAML bindings access them via instance.Path).
            PropertyInfo? p = ReflectionMembers.GetProperty(t, name,
                BindingFlags.Public | BindingFlags.Instance);
            if (p != null)
            {
                current = p.GetValue(current);
                return true;
            }
            p = ReflectionMembers.GetProperty(t, name,
                BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            if (p != null)
            {
                current = p.GetValue(null);
                return true;
            }

            // Fallback: a public field (rare in WP code but cheap to check).
            FieldInfo? f = t.GetField(name,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            if (f != null)
            {
                current = f.GetValue(f.IsStatic ? null : current);
                return true;
            }
            return false;
        }

        /// <summary>Applies "[0][key]" to <paramref name="current"/>; false when an index is missing.</summary>
        private bool ApplyIndexers(ref object? current, string indexers)
        {
            int at = 0;
            while (at < indexers.Length)
            {
                if (indexers[at] != '[') return false;
                int close = indexers.IndexOf(']', at);
                if (close < 0 || current == null) return false;
                string argument = indexers.Substring(at + 1, close - at - 1).Trim();
                at = close + 1;

                if (current is System.Collections.Specialized.INotifyCollectionChanged observable)
                {
                    observable.CollectionChanged += OnPathCollectionChanged;
                    _collectionHooks.Add(observable);
                }

                if (current is System.Collections.IList list && int.TryParse(argument, out int index))
                {
                    if (index < 0 || index >= list.Count) return false;
                    current = list[index];
                    continue;
                }

                // Any one-argument indexer taking an int or a string (a dictionary's).
                object? next = null;
                bool found = false;
                foreach (PropertyInfo p in current.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    ParameterInfo[] parameters = p.GetIndexParameters();
                    if (parameters.Length != 1) continue;
                    Type type = parameters[0].ParameterType;
                    object? key = type == typeof(int) && int.TryParse(argument, out int i) ? i
                        : type == typeof(string) || type == typeof(object) ? argument
                        : null;
                    if (key == null) continue;
                    try { next = p.GetValue(current, new[] { key }); found = true; }
                    catch (TargetInvocationException) { return false; }
                    break;
                }
                if (!found) return false;
                current = next;
            }
            return true;
        }

        private static readonly object NotConvertible = new();

        private static object? CoerceToTargetType(object? value, Type target)
        {
            if (value == null) return null;
            if (target.IsAssignableFrom(value.GetType())) return value;
            // A string or Uri bound to a Uri property (BitmapImage.UriSource).
            if (target == typeof(Uri))
            {
                string s = value.ToString() ?? string.Empty;
                return Uri.TryCreate(s, UriKind.RelativeOrAbsolute, out Uri? uri) ? uri : value;
            }
            // A Uri bound to Image.Source / ImageBrush.ImageSource, as Silverlight's converter allows.
            if (value is Uri imageUri && typeof(ImageSource).IsAssignableFrom(target))
                return new WPR.WindowsCompability.BitmapImage(imageUri);
            // Light-touch coercion: numeric/string round-trip via XamlTypeConverter when possible.
            try
            {
                if (value is string s) return XamlTypeConverter.Convert(s, target);
                return Convert.ChangeType(value, target);
            }
            catch
            {
                // Not convertible: storing it would put a value of the wrong type in the property,
                // and every later read of it would throw. Leave the property as it was.
                System.Diagnostics.Trace.WriteLine(
                    $"[Binding] cannot convert {value.GetType().Name} to {target.Name}; value dropped");
                return NotConvertible;
            }
        }
    }
}
