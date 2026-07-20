using System.Globalization;
using System.Windows.Data;

namespace CalDesk.App.Converters;

public sealed class DateEqualsConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        return values.Length >= 2
            && values[0] is DateOnly cellDate
            && values[1] is DateOnly selectedDate
            && cellDate == selectedDate;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
