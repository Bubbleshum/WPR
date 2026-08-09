using System;

namespace WPR.SilverlightCompability
{
    /// <summary>Shim for <c>System.Windows.Data.IValueConverter</c>.</summary>
    public interface IValueConverter
    {
        object? Convert(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture);
        object? ConvertBack(object? value, Type targetType, object? parameter, System.Globalization.CultureInfo culture);
    }
}
