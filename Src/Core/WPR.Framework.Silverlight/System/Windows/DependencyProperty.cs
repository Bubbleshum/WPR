using System;

namespace WPR.SilverlightCompability
{
    public sealed class DependencyProperty
    {
        public string Name { get; }
        public Type PropertyType { get; }
        public Type OwnerType { get; }
        public PropertyMetadata DefaultMetadata { get; }

        private DependencyProperty(string name, Type propertyType, Type ownerType, PropertyMetadata? metadata)
        {
            Name = name;
            PropertyType = propertyType;
            OwnerType = ownerType;
            DefaultMetadata = metadata ?? new PropertyMetadata(GetTypeDefault(propertyType));

            // Metadata built with only a callback has no default. Silverlight then uses the type's
            // own default, so an int property reads 0, not null: the toolkit's Pivot registers
            // SelectedIndex that way and unboxes it, which was a NullReferenceException.
            if (DefaultMetadata.DefaultValue == null && propertyType.IsValueType && Nullable.GetUnderlyingType(propertyType) == null)
                DefaultMetadata.DefaultValue = GetTypeDefault(propertyType);
        }

        public static DependencyProperty Register(string name, Type propertyType, Type ownerType, PropertyMetadata? typeMetadata = null)
            => new(name, propertyType, ownerType, typeMetadata);

        public static DependencyProperty RegisterAttached(string name, Type propertyType, Type ownerType, PropertyMetadata? defaultMetadata = null)
            => new(name, propertyType, ownerType, defaultMetadata);

        private static object? GetTypeDefault(Type t)
            => t.IsValueType ? Activator.CreateInstance(t) : null;

        public override string ToString() => $"{OwnerType.Name}.{Name}";

        /// <summary>What <see cref="DependencyObject.ReadLocalValue"/> answers for a property never set.</summary>
        public static readonly object UnsetValue = new UnsetValueMarker();

        private sealed class UnsetValueMarker
        {
            public override string ToString() => "{DependencyProperty.UnsetValue}";
        }
    }
}
