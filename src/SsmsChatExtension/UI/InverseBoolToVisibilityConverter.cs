using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace SsmsChatExtension.UI
{
    /// <summary>true -> Collapsed, false -> Visible (shows Send while no reply is running).</summary>
    public sealed class InverseBoolToVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value is bool b && b ? Visibility.Collapsed : Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotSupportedException();
        }
    }
}
