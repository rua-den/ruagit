using System;
using System.Globalization;

using Avalonia;
using Avalonia.Data.Converters;

namespace SourceGit.Converters
{
    public class EnumToBooleanConverter : IValueConverter
    {
        public static readonly EnumToBooleanConverter Instance = new();

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value == null || parameter == null)
                return false;

            return value.Equals(Enum.Parse(value.GetType(), parameter.ToString(), true));
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool b && b && parameter != null && targetType.IsEnum)
                return Enum.Parse(targetType, parameter.ToString(), true);

            return AvaloniaProperty.UnsetValue;
        }
    }
}