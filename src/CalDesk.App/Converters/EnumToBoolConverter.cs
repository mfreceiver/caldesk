using System.Globalization;
using System.Windows.Data;

namespace CalDesk.App.Converters;

/// <summary>Two-way enum↔bool converter for binding a group of RadioButtons to a single enum
/// property. ConverterParameter is the enum value each RadioButton represents.</summary>
public sealed class EnumToBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not null && value.Equals(parameter);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? parameter : Binding.DoNothing;
}
