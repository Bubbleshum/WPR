using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;

namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Style</c>. Carries setters keyed by
    /// property name (or DependencyProperty). <see cref="Apply"/> applies the
    /// collected setters to a target FrameworkElement — called when an element's
    /// Style property changes (FrameworkElement.OnStyleChanged).</summary>
    [ContentProperty(nameof(Setters))]
    public class Style
    {
        public Type? TargetType { get; set; }
        public Style? BasedOn { get; set; }
        public List<Setter> Setters { get; } = new List<Setter>();

        /// <summary>
        /// Apply this style (and the chain of <see cref="BasedOn"/> styles) to
        /// <paramref name="target"/>. BasedOn applies first so the closest style
        /// wins on conflicts. Property strings are resolved against the runtime
        /// type's DPs first, falling back to CLR properties.
        /// </summary>
        public void Apply(FrameworkElement target)
        {
            if (target == null) return;
            BasedOn?.Apply(target);

            Type t = target.GetType();
            foreach (Setter s in Setters)
            {
                if (s.Property is not string propName || string.IsNullOrEmpty(propName)) continue;
                try { ApplyOne(target, t, propName, s.Value); }
                catch { /* one bad setter shouldn't take down the whole apply */ }
            }
        }

        private static void ApplyOne(FrameworkElement target, Type t, string propName, object? value)
        {
            // Strip "Class.Property" qualifier when present (e.g. "TextBlock.FontSize").
            int dot = propName.IndexOf('.');
            if (dot >= 0) propName = propName.Substring(dot + 1);

            // DependencyProperty first.
            FieldInfo? dpField = null;
            for (Type? cur = t; cur != null && dpField == null; cur = cur.BaseType)
            {
                dpField = cur.GetField(propName + "Property",
                    BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            }
            if (dpField?.GetValue(null) is DependencyProperty dp)
            {
                object? converted = ConvertForTarget(value, dp.PropertyType);
                target.SetValue(dp, converted);
                return;
            }

            // CLR property fallback.
            PropertyInfo? prop = t.GetProperty(propName,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
            if (prop != null && prop.CanWrite)
            {
                object? converted = ConvertForTarget(value, prop.PropertyType);
                prop.SetValue(target, converted);
            }
        }

        private static object? ConvertForTarget(object? value, Type targetType)
        {
            if (value == null) return null;
            if (targetType.IsInstanceOfType(value)) return value;
            if (value is string str) return XamlTypeConverter.Convert(str, targetType);
            return value;
        }
    }
}
