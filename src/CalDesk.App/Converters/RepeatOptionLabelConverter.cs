using System.Globalization;
using System.Windows.Data;
using CalDesk.App.ViewModels;

namespace CalDesk.App.Converters;

/// <summary>One-way display label for <see cref="RecurrencePick"/> combo items. Only Custom
/// gets a dedicated label ("自定义（保留）" — a non-preset rule that saving keeps as-is, CQ-001);
/// the preset names keep their existing display so this adds the placeholder without
/// altering current UI text.</summary>
public sealed class RepeatOptionLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is RecurrencePick.Custom ? "自定义（保留）" : value?.ToString() ?? string.Empty;

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
