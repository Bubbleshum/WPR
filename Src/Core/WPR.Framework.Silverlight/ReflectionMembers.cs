using System;
using System.Reflection;

namespace WPR.SilverlightCompability
{
    /// <summary>Reflection lookups the XAML reader and bindings share.</summary>
    internal static class ReflectionMembers
    {
        /// <summary>
        /// <see cref="Type.GetProperty(string, BindingFlags)"/>, except that a property a derived
        /// class hides with <c>new</c> and a different type resolves to the most-derived one, as C#
        /// does, instead of throwing <see cref="AmbiguousMatchException"/>. WP8's
        /// <c>LongListSelector.ItemsSource</c> (an <c>IList</c>) over ItemsControl's
        /// (<c>IEnumerable</c>) is the case: <c>ItemsSource="{Binding …}"</c> threw in the reader.
        /// </summary>
        public static PropertyInfo? GetProperty(Type type, string name, BindingFlags flags)
        {
            try { return type.GetProperty(name, flags); }
            catch (AmbiguousMatchException)
            {
                for (Type? t = type; t != null; t = t.BaseType)
                {
                    PropertyInfo? p = t.GetProperty(name, (flags & ~BindingFlags.FlattenHierarchy) | BindingFlags.DeclaredOnly);
                    if (p != null) return p;
                }
                return null;
            }
        }
    }
}
