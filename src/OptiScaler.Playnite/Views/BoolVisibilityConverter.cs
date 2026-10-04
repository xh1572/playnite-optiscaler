using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace OptiScaler.Playnite.Views
{
    /// <summary>Maps true to Visible and false to Collapsed; ConverterParameter "Invert" flips it.</summary>
    public sealed class BoolVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            var visible = value is bool flag && flag;
            if (string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase)) visible = !visible;
            return visible ? Visibility.Visible : Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => Binding.DoNothing;
    }
}
