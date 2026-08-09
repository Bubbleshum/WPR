namespace WPR.SilverlightCompability
{
    public class PropertyMetadata
    {
        public object? DefaultValue { get; }
        public PropertyChangedCallback? PropertyChangedCallback { get; }

        public PropertyMetadata(object? defaultValue)
        {
            DefaultValue = defaultValue;
        }

        public PropertyMetadata(PropertyChangedCallback propertyChangedCallback)
        {
            PropertyChangedCallback = propertyChangedCallback;
        }

        public PropertyMetadata(object? defaultValue, PropertyChangedCallback propertyChangedCallback)
        {
            DefaultValue = defaultValue;
            PropertyChangedCallback = propertyChangedCallback;
        }
    }
}
