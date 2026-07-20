using System.Globalization;
using System.Windows.Data;

namespace CalDesk.App.Converters;

/// <summary>IMultiValueConverter — OrdinalIgnoreCase-compares two string values.
/// Returns true when values[0] equals values[1]. Used by §3.12 S3 swatch selection
/// (DataTrigger compares swatch hex against the bound DataContext.Color).</summary>
public sealed class EqualsConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not string a || values[1] is not string b) return false;
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
