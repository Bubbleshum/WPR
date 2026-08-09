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
        }

        public static DependencyProperty Register(string name, Type propertyType, Type ownerType, PropertyMetadata? typeMetadata = null)
            => new(name, propertyType, ownerType, typeMetadata);

        public static DependencyProperty RegisterAttached(string name, Type propertyType, Type ownerType, PropertyMetadata? defaultMetadata = null)
            => new(name, propertyType, ownerType, defaultMetadata);

        private static object? GetTypeDefault(Type t)
            => t.IsValueType ? Activator.CreateInstance(t) : null;

        public override string ToString() => $"{OwnerType.Name}.{Name}";
    }
}
