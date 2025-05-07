using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace RepoToTxtGui
{
    public class ZeroToCollapsedConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is int count)
            {
                // Parameter "InverseZeroToCollapsed" means: if count is 0, then Collapsed. Otherwise Visible.
                // Default behavior (no parameter or other string): if count is 0, Visible. Otherwise Collapsed.
                bool inverse = parameter as string == "InverseZeroToCollapsed";
                if (inverse)
                {
                    return count == 0 ? Visibility.Collapsed : Visibility.Visible;
                }
                else
                {
                    return count == 0 ? Visibility.Visible : Visibility.Collapsed;
                }
            }
            return Visibility.Collapsed; // Default to collapsed if value is not an int
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}